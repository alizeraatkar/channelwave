// View changes behave like page navigation for keyboard and screen-reader users.
let activityChanged;
let homeScroll;
const scrollSaveDelay = 150;
export async function captureHomeScroll() { await homeScroll?.remember(); }
export function watchHomeScroll(reference, account, view, top) {
    const content = document.getElementById('main-content');
    const home = content?.querySelector('.music-home');
    if (!home) return { dispose: async () => {} };
    const selectedTab = home.querySelector('[role="tab"][aria-selected="true"]');
    const isCurrentView = () => home.isConnected && home.querySelector('[role="tab"][aria-selected="true"]') === selectedTab;
    let frame, timer, saveTimer, restoring = true, disposed = false, savedTop = top;
    let pendingSave = Promise.resolve();
    content.dataset.homeRestoring = 'true';
    const sample = () => {
        if (disposed || restoring || !isCurrentView()) return;
        state.top = content.scrollTop;
    };
    const flush = () => {
        clearTimeout(saveTimer);
        if (state.top !== savedTop) {
            const value = savedTop = state.top;
            pendingSave = pendingSave.then(() => reference.invokeMethodAsync('RememberScrollAsync', account, view, value)).catch(() => {});
        }
        return pendingSave;
    };
    const state = { top, remember: () => { sample(); return flush(); } };
    homeScroll = state;
    const stopRestoring = () => {
        restoring = false; observer.disconnect(); clearTimeout(timer);
        content.removeEventListener('channelwaveviewportsettled', settled);
        if (homeScroll === state) delete content.dataset.homeRestoring;
    };
    const restore = () => {
        if (disposed) return;
        if (!isCurrentView()) { stopRestoring(); return; }
        content.scrollTo({ top, behavior: 'instant' });
        if (!content.querySelector('.virtual-tracks') && content.scrollHeight - content.clientHeight >= top - 2) stopRestoring();
    };
    const settled = () => {
        restore();
        if (content.scrollHeight - content.clientHeight >= top - 2) stopRestoring();
    };
    content.addEventListener('channelwaveviewportsettled', settled);
    const observer = new MutationObserver(() => { cancelAnimationFrame(frame); frame = requestAnimationFrame(restore); });
    observer.observe(content, { childList: true, subtree: true });
    timer = setTimeout(stopRestoring, 2000);
    frame = requestAnimationFrame(restore);
    // Scroll handlers only sample a number. Bridge calls wait until the gesture
    // settles, or an explicit capture before navigation/backgrounding.
    const remember = () => {
        if (disposed || restoring || !isCurrentView()) return;
        sample(); clearTimeout(saveTimer);
        saveTimer = setTimeout(flush, scrollSaveDelay);
    };
    content.addEventListener('scroll', remember, { passive: true });
    return { dispose: async () => {
        // The next tab may already have rendered: persist the cached position.
        const saved = flush();
        disposed = true; cancelAnimationFrame(frame); stopRestoring();
        content.removeEventListener('scroll', remember);
        if (homeScroll === state) homeScroll = null;
        await saved;
    } };
}
export function watchActivity(reference) {
    stopWatchingActivity();
    activityChanged = () => reference.invokeMethodAsync('SetActivityAsync', document.visibilityState !== 'hidden').catch(() => {});
    document.addEventListener('visibilitychange', activityChanged);
}
export function stopWatchingActivity() { if (activityChanged) document.removeEventListener('visibilitychange', activityChanged); activityChanged = null; }
globalThis.channelwaveFocusQueueEntry = (id, action) => {
    const row = document.querySelector(`[data-queue-entry="${CSS.escape(id)}"]`);
    const buttons = row?.querySelectorAll('button');
    const button = buttons?.[action];
    (button && !button.disabled && button.getClientRects().length ? button : row?.querySelector('summary') ?? buttons?.[0] ?? document.querySelector('.queue-panel > header button'))?.focus({ preventScroll: true });
};
export function focusPage() {
    const content = document.getElementById('main-content');
    const snapshot = pendingSnapshot;
    pendingSnapshot = null;
    let observer, timer;
    const stop = () => { observer?.disconnect(); clearTimeout(timer); };
    const restore = () => {
        const target = snapshot?.focus ? document.getElementById(snapshot.focus) : snapshot?.target
            ? [...content.querySelectorAll('button,a,input,select,summary')].find(el => el.tagName === snapshot.target.tag
                && (el.getAttribute('aria-label') || el.textContent.trim()) === snapshot.target.label
                && (!snapshot.target.track || el.closest('[data-track-key]')?.dataset.trackKey === snapshot.target.track)) : null;
        content?.scrollTo({ top: snapshot?.scroll ?? (currentPage()?.split('~')[0] === 'home' ? homeScroll?.top : 0) ?? 0, behavior: 'instant' });
        const search = currentPage()?.split('~')[0] === 'search' ? document.getElementById('music-search') : null;
        (target || search || content)?.focus({ preventScroll: true });
        if ((!snapshot?.target || target) && (!snapshot?.scroll || content.scrollHeight - content.clientHeight >= snapshot.scroll - 2)) stop();
    };
    if (snapshot?.scroll || snapshot?.target) {
        observer = new MutationObserver(() => requestAnimationFrame(restore));
        observer.observe(content, { subtree: true, childList: true });
        timer = setTimeout(stop, 2000);
    }
    requestAnimationFrame(restore);
}

export function focusHomeTab(id) {
    requestAnimationFrame(() => {
        const tab = document.getElementById(`home-tab-${id}`);
        tab?.focus({ preventScroll: true });
        tab?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
    });
}

let resize, changes, keepFocusVisible, layoutFrame, tabKeys;
export function afterPaint() {
    return new Promise(resolve => {
        let first, second;
        const done = () => { clearTimeout(timer); cancelAnimationFrame(first); cancelAnimationFrame(second); resolve(); };
        const timer = setTimeout(done, 100); // Background tabs may suspend animation frames.
        first = requestAnimationFrame(() => { second = requestAnimationFrame(done); });
    });
}
// Fixed controls grow with text scaling, translated labels, and error messages.
export function watchLayout() {
    stopWatchingLayout();
    let observed = [];
    const update = () => {
        pruneDialogs();
        const nav = document.querySelector('.mobile-nav');
        const player = document.querySelector('.music-player') ?? document.querySelector('.idle-player');
        const transfer = document.querySelector('.music-transfer');
        const next = [nav, player, transfer].filter(Boolean);
        for (const element of observed) if (!next.includes(element)) resize.unobserve(element);
        for (const element of next) if (!observed.includes(element)) resize.observe(element);
        observed = next;
        const rects = new Map(next.map(element => [element, element.getBoundingClientRect()]));
        const overlays = [...rects.values()].filter(rect => rect.height > 0);
        const bottom = Math.min(innerHeight, ...overlays.map(rect => rect.top));
        const headerHeight = document.querySelector('.app-topbar')?.getBoundingClientRect().height ?? 72;
        // Read all geometry before changing the app frame.
        document.documentElement.style.setProperty('--mobile-nav-height', `${rects.get(nav)?.height ?? 0}px`);
        document.documentElement.style.setProperty('--player-height', `${rects.get(player)?.height ?? 0}px`);
        document.documentElement.style.setProperty('--transfer-height', `${rects.get(transfer)?.height ?? 0}px`);
        document.documentElement.style.setProperty('--bottom-clearance', `${innerHeight - bottom + 12}px`);
        document.documentElement.style.setProperty('--header-height', `${headerHeight}px`);
    };
    const schedule = () => { layoutFrame ??= requestAnimationFrame(() => { layoutFrame = undefined; update(); }); };
    resize = new ResizeObserver(schedule);
    changes = new MutationObserver(records => {
        const overlays = '.music-player, .idle-player, .music-transfer, .mobile-nav';
        if (records.some(record => [...record.addedNodes, ...record.removedNodes].some(node => node instanceof Element
            && (node.matches(overlays) || node.querySelector(overlays))))) schedule();
    });
    const shell = document.querySelector('.app-shell');
    if (shell) changes.observe(shell, { childList: true, subtree: true });
    const header = document.querySelector('.app-topbar');
    if (header) resize.observe(header);
    keepFocusVisible = () => requestAnimationFrame(() => {
        const target = document.activeElement;
        if (!(target instanceof HTMLElement) || !target.matches(':focus-visible') || !target.closest('#main-content') || target.closest('.music-player, .music-transfer, dialog') || target.id === 'main-content') return;
        // Moving a pointer-focused row between pointerdown and pointerup cancels
        // its click. Extra clearance is needed for keyboard focus only.
        // Keyboard focus must also reveal horizontally scrolled filter chips.
        // Finish the browser's implicit scroll before measuring fixed overlays.
        target.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
        update();
        const focused = target.getBoundingClientRect();
        const content = document.getElementById('main-content');
        const panel = content.getBoundingClientRect();
        const top = panel.top + 8;
        const bottom = panel.bottom - 8;
        const row = target.closest('.music-track')?.getBoundingClientRect();
        const rect = row && row.height <= bottom - top ? row : focused;
        if (rect.bottom > bottom) content.scrollBy({ top: rect.bottom - bottom, behavior: 'instant' });
        else if (rect.top < top) content.scrollBy({ top: rect.top - top, behavior: 'instant' });
    });
    document.addEventListener('focusin', keepFocusVisible);
    tabKeys = event => {
        if (!event.target.matches('.home-tab-strip [role=tab]') || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const tabs = [...event.target.closest('[role=tablist]').querySelectorAll('[role=tab]')];
        const index = tabs.indexOf(event.target);
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
        tabs[next].focus({ preventScroll: true }); tabs[next].scrollIntoView({ block: 'nearest', inline: 'nearest' }); tabs[next].click();
    };
    document.addEventListener('keydown', tabKeys);
    update();
}
export function stopWatchingLayout() {
    cancelAnimationFrame(layoutFrame); layoutFrame = undefined;
    resize?.disconnect();
    changes?.disconnect();
    if (keepFocusVisible) document.removeEventListener('focusin', keepFocusVisible);
    if (tabKeys) document.removeEventListener('keydown', tabKeys);
}

const pages = new Set(['home', 'settings', 'profile', 'customize', 'about', 'tracks', 'playlists', 'search', 'library', 'favorites', 'playlist', 'offline', 'channels', 'account', 'downloads', 'storage', 'activity', 'history', 'statistics']);
let restorePage, navigationReference, pendingSnapshot, restoringRoute = false, rememberScroll;
let navigationScrollTimer, navigationContent, navigationVisibilityChanged;
function currentPage() { const params = new URLSearchParams(location.hash.slice(1)), page = params.get('view'); return params.has('browse') ? `${page}~${params.get('browse')}` : page; }
function validPage(page) { return pages.has(page?.split('~')[0]); }
function pageUrl(page) { const split = page.indexOf('~'), view = split < 0 ? page : page.slice(0, split), browse = split < 0 ? null : page.slice(split + 1), params = new URLSearchParams({ view }); if (browse) params.set('browse', browse); return `${location.pathname}${location.search}#${params}`; }
export function capturePage() {
    clearTimeout(navigationScrollTimer);
    if (restoringRoute) return;
    const state = history.state || {};
    const active = document.activeElement;
    history.replaceState({ ...state, channelwave: { ...(state.channelwave || { index: 0 }),
        loaded: Math.max(100, ...[...document.querySelectorAll('#main-content [data-loaded-count]')].filter(el => el.getClientRects().length).map(el => Number(el.dataset.loadedCount))),
        scroll: document.getElementById('main-content')?.scrollTop || 0, focus: active?.id || null,
        target: active?.matches('button,a,input,select,summary') ? { tag: active.tagName, label: active.getAttribute('aria-label') || active.textContent.trim(), track: active.closest('[data-track-key]')?.dataset.trackKey } : null } }, '', location.href);
}
export function restoredLoaded() { return pendingSnapshot?.loaded || 100; }
export function watchNavigation(reference, fallback) {
    stopWatchingNavigation();
    navigationReference = reference;
    const initial = currentPage();
    const page = validPage(initial) ? initial : fallback;
    pendingSnapshot = history.state?.channelwave;
    history.replaceState({ channelwave: pendingSnapshot || { index: 0 } }, '', pageUrl(page));
    restoringRoute = true;
    restorePage = () => {
        // A timer from the page being left must never overwrite the entry that
        // the browser has just restored.
        clearTimeout(navigationScrollTimer);
        pendingSnapshot = history.state?.channelwave;
        restoringRoute = true;
        reference.invokeMethodAsync('RestorePageAsync', validPage(currentPage()) ? currentPage() : fallback);
    };
    window.addEventListener('popstate', restorePage);
    rememberScroll = () => {
        clearTimeout(navigationScrollTimer);
        if (!pendingSnapshot && !restoringRoute) navigationScrollTimer = setTimeout(capturePage, scrollSaveDelay);
    };
    navigationContent = document.getElementById('main-content');
    navigationContent?.addEventListener('scroll', rememberScroll, { passive: true });
    navigationVisibilityChanged = () => { if (document.visibilityState === 'hidden') flushNavigation(); };
    document.addEventListener('visibilitychange', navigationVisibilityChanged);
    window.addEventListener('pagehide', flushNavigation);
    return page;
}
export function recordPage(page) {
    if (!validPage(page)) return;
    if (restoringRoute) {
        restoringRoute = false;
        history.replaceState(history.state, '', pageUrl(page));
    } else if (currentPage() !== page) {
        clearTimeout(navigationScrollTimer);
        const index = (history.state?.channelwave?.index || 0) + 1;
        history.pushState({ channelwave: { index, scroll: 0 } }, '', pageUrl(page));
    }
}
export function goBack() {
    if (!(history.state?.channelwave?.index > 0)) return false;
    flushNavigation();
    history.back();
    return true;
}
window.channelwaveHandleBack = () => {
    if (window.channelwaveDismissDialog?.()) return true;
    const page = currentPage()?.split('~')[0];
    if (['home', 'library', 'settings', 'account', 'profile'].includes(page)) return false;
    if (goBack()) return true;
    restoringRoute = true;
    navigationReference?.invokeMethodAsync('NavigateBackAsync');
    return !!navigationReference;
};
export function stopWatchingNavigation() {
    if (navigationReference && navigationContent?.isConnected) flushNavigation();
    clearTimeout(navigationScrollTimer);
    if (restorePage) window.removeEventListener('popstate', restorePage);
    navigationContent?.removeEventListener('scroll', rememberScroll);
    window.removeEventListener('pagehide', flushNavigation);
    if (navigationVisibilityChanged) document.removeEventListener('visibilitychange', navigationVisibilityChanged);
    restorePage = null; navigationReference = null; navigationContent = null;
}
function flushNavigation() { capturePage(); void captureHomeScroll(); }

const dialogs = new WeakMap();
const dialogStack = [];
function pruneDialogs() {
    for (const dialog of [...dialogStack]) if (!dialog.isConnected) closeLibraryDialog(dialog);
}
// Android delegates Back here before letting MAUI perform normal navigation.
window.channelwaveDismissDialog = () => {
    pruneDialogs();
    const dialog = [...dialogStack].reverse().find(element => element.open);
    if (!dialog) return false;
    dialog.dispatchEvent(new Event('cancel', { cancelable: true }));
    return true;
};
export function openLibraryDialog(dialog, reference) {
    if (dialog.open) return;
    const previous = document.activeElement;
    const cancel = event => { event.preventDefault(); reference.invokeMethodAsync('RequestCloseAsync'); };
    let pressedBackdrop = false;
    const outside = event => {
        const rect = dialog.getBoundingClientRect();
        return event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom;
    };
    const pointerdown = event => { pressedBackdrop = event.target === dialog && outside(event); };
    const click = event => {
        if (pressedBackdrop && event.target === dialog && outside(event)) cancel(event);
        pressedBackdrop = false;
    };
    const handle = dialog.querySelector('[data-dialog-handle]');
    let dragY = null;
    const dragStart = event => {
        if (!matchMedia('(max-width: 760px)').matches) return;
        dragY = event.clientY; handle.setPointerCapture(event.pointerId);
    };
    const dragMove = event => {
        if (dragY !== null) dialog.style.setProperty('--sheet-drag', `${Math.max(0, event.clientY - dragY)}px`);
    };
    const dragEnd = event => {
        const dismiss = dragY !== null && event.type !== 'pointercancel' && event.clientY - dragY > 80;
        dragY = null;
        if (dismiss) cancel(event);
        else dialog.style.removeProperty('--sheet-drag');
    };
    handle?.addEventListener('pointerdown', dragStart);
    handle?.addEventListener('pointermove', dragMove);
    handle?.addEventListener('pointerup', dragEnd);
    handle?.addEventListener('pointercancel', dragEnd);
    dialogs.set(dialog, { previous, cancel, pointerdown, click, handle, dragStart, dragMove, dragEnd });
    dialogStack.push(dialog);
    document.documentElement.classList.add('modal-open');
    dialog.addEventListener('cancel', cancel);
    dialog.addEventListener('pointerdown', pointerdown);
    dialog.addEventListener('click', click);
    dialog.showModal();
    (dialog.querySelector('input:checked') ?? dialog.querySelector('input:not(:disabled), button:not(:disabled)'))?.focus();
}
export function closeLibraryDialog(dialog) {
    if (!dialog) { pruneDialogs(); return; }
    const state = dialogs.get(dialog);
    if (!state) return;
    // Navigation/removal can dismiss a player with an action sheet above it.
    const parentIndex = dialogStack.indexOf(dialog);
    for (const child of [...dialogStack].slice(parentIndex + 1).reverse()) closeLibraryDialog(child);
    dialog.removeEventListener('cancel', state.cancel);
    dialog.removeEventListener('pointerdown', state.pointerdown);
    dialog.removeEventListener('click', state.click);
    state.handle?.removeEventListener('pointerdown', state.dragStart);
    state.handle?.removeEventListener('pointermove', state.dragMove);
    state.handle?.removeEventListener('pointerup', state.dragEnd);
    state.handle?.removeEventListener('pointercancel', state.dragEnd);
    dialog.style.removeProperty('--sheet-drag');
    if (dialog.open) dialog.close();
    const index = dialogStack.indexOf(dialog);
    if (index >= 0) dialogStack.splice(index, 1);
    if (!dialogStack.length) document.documentElement.classList.remove('modal-open');
    // Closing a dialog on a hidden page must not move focus away from navigation.
    if (state.previous?.isConnected && state.previous.getClientRects().length) state.previous.focus({ preventScroll: true });
    else if (!dialogStack.length) {
        const fallback = document.querySelector('.music-player-info') ?? document.getElementById('main-content');
        if (fallback?.getClientRects().length) fallback.focus({ preventScroll: true });
    }
    dialogs.delete(dialog);
}
export function openTagDialog(dialog, reference) { openLibraryDialog(dialog, reference); dialog.querySelector('input')?.focus(); }
export function closeTagDialog(dialog) { closeLibraryDialog(dialog); }
