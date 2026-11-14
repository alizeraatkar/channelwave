const viewports = new WeakMap();
const gap = 8;

export function watchViewport(element, reference) {
    unwatchViewport(element);
    const root = element.closest('#main-content');
    if (!root) return;
    const state = { element, root, reference, keys: [], indices: new Map(), heights: new Map(), offsets: [0],
        generation: 0, enabled: false, canLoadMore: false, loading: false, error: false,
        requestedWindowRevision: Number(element.dataset.windowRevision || 0),
        frame: 0, signature: '', requested: -1, observed: new Set(), pinned: null, disposed: false, width: root.clientWidth };
    const schedule = () => { if (!state.frame && !state.disposed) state.frame = requestAnimationFrame(() => update(state)); };
    state.schedule = schedule;
    state.resize = new ResizeObserver(schedule);
    state.resize.observe(root);
    state.changes = new MutationObserver(schedule);
    state.changes.observe(element, { childList: true });
    state.focus = () => {
        const row = document.activeElement?.closest('[data-track-key]');
        if (row && element.contains(row)) state.pinned = row.dataset.trackKey;
        else if (!document.querySelector('dialog[open]')) state.pinned = null;
        schedule();
    };
    root.addEventListener('scroll', schedule, { passive: true });
    document.addEventListener('focusin', state.focus);
    document.addEventListener('focusout', schedule);
    viewports.set(element, state);
}

export function configureViewport(element, keys, generation, enabled, canLoadMore, loading, error) {
    const state = viewports.get(element);
    if (!state) return;
    if (generation !== state.generation) {
        state.generation = generation; state.signature = ''; state.requested = -1; state.pinned = null;
        state.requestedWindowRevision = Number(element.dataset.windowRevision || 0);
        state.heights.clear(); state.keys = []; state.offsets = [0];
    }
    if (keys) {
        const anchor = getAnchor(state);
        state.keys = keys; state.indices = new Map(keys.map((key, index) => [key, index]));
        for (const key of state.heights.keys()) if (!state.indices.has(key)) state.heights.delete(key);
        rebuild(state); restoreAnchor(state, anchor);
    }
    if (state.loading && !loading && !error) state.requested = -1;
    Object.assign(state, { enabled, canLoadMore, loading, error });
    state.schedule();
}
export function refreshViewport(element) { viewports.get(element)?.schedule(); }
export function unwatchViewport(element) {
    const state = viewports.get(element);
    if (!state) return;
    state.disposed = true; cancelAnimationFrame(state.frame);
    state.resize.disconnect(); state.changes.disconnect();
    state.root.removeEventListener('scroll', state.schedule);
    document.removeEventListener('focusin', state.focus); document.removeEventListener('focusout', state.schedule);
    viewports.delete(element);
}

function position(state) { return state.root.getBoundingClientRect().top - state.element.getBoundingClientRect().top; }
function indexAt(state, offset) {
    let low = 0, high = state.keys.length;
    while (low < high) { const middle = (low + high) >>> 1; if (state.offsets[middle + 1] <= offset) low = middle + 1; else high = middle; }
    return Math.min(low, Math.max(0, state.keys.length - 1));
}
function getAnchor(state) {
    if (!state.keys.length || !state.enabled) return null;
    const top = position(state);
    if (top < 0) return null;
    const index = indexAt(state, top);
    return { key: state.keys[index], offset: state.offsets[index] };
}
function restoreAnchor(state, anchor) {
    if (state.root.dataset.homeRestoring === 'true') return;
    if (!anchor || !state.indices.has(anchor.key)) return;
    const delta = state.offsets[state.indices.get(anchor.key)] - anchor.offset;
    if (Math.abs(delta) > .5) state.root.scrollTop += delta;
}
function rebuild(state) {
    const columns = state.element.classList.contains('virtual-grid')
        ? Math.max(1, getComputedStyle(state.element).gridTemplateColumns.split(' ').length) : 1;
    state.columns = columns;
    const estimate = columns > 1 ? state.element.clientWidth / columns + 120 : matchMedia('(max-width: 1099px)').matches ? 124 : 88;
    const offsets = new Array(state.keys.length + 1).fill(0);
    let height = 0;
    for (let start = 0; start < state.keys.length; start += columns) {
        let rowHeight = 0;
        for (let i = start; i < Math.min(start + columns, state.keys.length); i++) {
            offsets[i] = height;
            rowHeight = Math.max(rowHeight, state.heights.get(state.keys[i]) ?? estimate);
        }
        height += rowHeight;
    }
    offsets[state.keys.length] = height;
    state.offsets = offsets;
}
function update(state) {
    state.frame = 0;
    if (state.disposed || !state.enabled || !state.element.getClientRects().length) return;
    const anchor = getAnchor(state), rows = [...state.element.querySelectorAll(':scope > [data-track-key]')];
    const width = state.root.clientWidth;
    let changed = false;
    if (Math.abs(width - state.width) > 1) { state.width = width; state.heights.clear(); changed = true; }
    const next = new Set(rows);
    for (const row of state.observed) if (!next.has(row)) state.resize.unobserve(row);
    // Read every size before any spacer/scroll write. ResizeObserver only queues
    // this frame; it never measures or invokes .NET inside its delivery loop.
    for (const row of rows) {
        if (!state.observed.has(row)) state.resize.observe(row);
        const key = row.dataset.trackKey, height = row.getBoundingClientRect().height + gap;
        if (height > gap && Math.abs((state.heights.get(key) ?? 0) - height) > .5) { state.heights.set(key, height); changed = true; }
    }
    state.observed = next;
    if (changed) { rebuild(state); restoreAnchor(state, anchor); }
    const top = position(state), height = state.root.clientHeight;
    const columns = state.columns || 1;
    const start = Math.floor(indexAt(state, Math.max(0, top - height)) / columns) * columns;
    const end = state.keys.length ? Math.min(state.keys.length, Math.ceil((indexAt(state, Math.max(0, top + height * 2)) + 1) / columns) * columns) : 0;
    const focused = document.activeElement?.closest('[data-track-key]');
    if (focused && state.element.contains(focused)) state.pinned = focused.dataset.trackKey;
    else if (!document.querySelector('dialog[open]')) state.pinned = null;
    const pinned = state.indices.get(state.pinned);
    const ranges = [{ start, end }];
    if (pinned !== undefined && (pinned < start || pinned >= end)) ranges.push({ start: Math.floor(pinned / columns) * columns, end: Math.min(state.keys.length, (Math.floor(pinned / columns) + 1) * columns) });
    ranges.sort((a, b) => a.start - b.start);
    let previousEnd = 0;
    const blocks = ranges.map(range => {
        const block = { ...range, before: state.offsets[range.start] - state.offsets[previousEnd] };
        previousEnd = range.end; return block;
    });
    const tail = state.offsets.at(-1) - state.offsets[previousEnd];
    const signature = JSON.stringify([blocks, tail]);
    if (signature !== state.signature) {
        state.signature = signature;
        state.requestedWindowRevision++;
        state.reference.invokeMethodAsync('WindowChangedAsync', blocks, tail, state.generation).catch(() => {});
    }
    // An unchanged request can still be awaiting its managed render. Home must
    // keep restoring until the corresponding rows/spacers are actually in DOM.
    else if (Number(state.element.dataset.windowRevision) >= state.requestedWindowRevision)
        state.root.dispatchEvent(new Event('channelwaveviewportsettled'));
    if (state.canLoadMore && !state.loading && !state.error && state.requested !== state.keys.length
        && top + height * 2 >= state.offsets.at(-1)) {
        state.requested = state.keys.length;
        state.reference.invokeMethodAsync('LoadMoreAsync', state.generation).catch(() => {});
    }
}
