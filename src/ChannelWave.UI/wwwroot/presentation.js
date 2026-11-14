const drags = new WeakMap();
export function watchTabDrag(element, reference) {
    stopTabDrag(element);
    let dragging = null;
    const down = event => {
        const handle = event.target.closest('.tab-drag-handle');
        if (!handle) return;
        const row = handle.closest('[data-tab-id]');
        dragging = { id: row.dataset.tabId, row };
        handle.setPointerCapture(event.pointerId);
        row.classList.add('tab-dragging');
    };
    const finish = event => {
        if (!dragging) return;
        const current = dragging; dragging = null;
        current.row.classList.remove('tab-dragging');
        if (event.type === 'pointercancel') return;
        const rows = [...element.querySelectorAll('[data-tab-id]')];
        const target = rows.findIndex(row => event.clientY < row.getBoundingClientRect().bottom);
        reference.invokeMethodAsync('Move', current.id, target < 0 ? rows.length - 1 : target);
    };
    element.addEventListener('pointerdown', down);
    element.addEventListener('pointerup', finish);
    element.addEventListener('pointercancel', finish);
    drags.set(element, () => {
        element.removeEventListener('pointerdown', down);
        element.removeEventListener('pointerup', finish);
        element.removeEventListener('pointercancel', finish);
    });
}
export function stopTabDrag(element) { drags.get(element)?.(); drags.delete(element); }
export function observeSection(element, reference) {
    const phone = matchMedia('(max-width: 760px)');
    const resize = () => reference.invokeMethodAsync('SetPreviewLimit', phone.matches ? 2 : 6);
    phone.addEventListener('change', resize);
    resize();
    const observer = new IntersectionObserver(entries => {
        if (!entries.some(entry => entry.isIntersecting)) return;
        observer.disconnect(); reference.invokeMethodAsync('LoadVisibleAsync');
    }, { root: document.getElementById('main-content'), rootMargin: '400px' });
    observer.observe(element);
    return { dispose: () => { observer.disconnect(); phone.removeEventListener('change', resize); } };
}
