const watchers = new WeakMap();
let visibility;
export function watchArtwork(element, reference, colorPlayerBackground = false) {
    unwatchArtwork(element);
    visibility ??= new IntersectionObserver(entries => {
        for (const entry of entries) if (entry.isIntersecting) {
            visibility.unobserve(entry.target);
            watchers.get(entry.target)?.reference.invokeMethodAsync('VisibleAsync').catch(() => {});
        }
    }, { rootMargin: '80px' });
    const stopColor = colorPlayerBackground ? watchPlayerColor(element) : () => {};
    watchers.set(element, { reference, disconnect() { visibility.unobserve(element); stopColor(); } }); visibility.observe(element);
}
export function unwatchArtwork(element) { watchers.get(element)?.disconnect(); watchers.delete(element); }

function luminance(rgb) {
    const linear = rgb.map(value => { const c = value / 255; return c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4; });
    return linear[0] * .2126 + linear[1] * .7152 + linear[2] * .0722;
}

function artworkColor(image) {
    const canvas = document.createElement('canvas'); canvas.width = canvas.height = 32;
    const context = canvas.getContext('2d', { willReadFrequently: true });
    if (!context) return null;
    context.drawImage(image, 0, 0, 32, 32);
    const pixels = context.getImageData(0, 0, 32, 32).data, bins = new Map();
    for (let i = 0; i < pixels.length; i += 4) {
        if (pixels[i + 3] < 128) continue;
        const rgb = [pixels[i], pixels[i + 1], pixels[i + 2]];
        // Ignore near-black borders and white lettering when a usable cover color exists.
        if (Math.max(...rgb) < 24 || Math.min(...rgb) > 240) continue;
        const key = (rgb[0] >> 4) << 8 | (rgb[1] >> 4) << 4 | rgb[2] >> 4;
        const bin = bins.get(key) ?? { count: 0, sum: [0, 0, 0] };
        bin.count++; rgb.forEach((value, channel) => bin.sum[channel] += value); bins.set(key, bin);
    }
    const dominant = [...bins.values()].sort((a, b) => b.count - a.count)[0];
    if (!dominant) return null;
    let rgb = dominant.sum.map(value => Math.round(value / dominant.count));
    // Bound the brightest gradient stop; every other stop is darker. This also
    // keeps white focus rings, green actions and secondary text above AA contrast.
    while (luminance(rgb) > .06) rgb = rgb.map(value => Math.floor(value * .96));
    return `rgb(${rgb.join(', ')})`;
}

function watchPlayerColor(element) {
    const player = element.closest('.full-screen-player, .music-player');
    if (!player) return () => {};
    let disposed = false;
    function update(event) {
        if (disposed || !element.isConnected) return;
        const image = element.querySelector('img');
        if (event && event.target !== image) return;
        let color = null;
        if (event?.type !== 'error' && image?.complete && image.naturalWidth && image.naturalHeight) {
            try { color = artworkColor(image); } catch { /* Invalid or unreadable artwork uses charcoal. */ }
        }
        if (color) player.style.setProperty('--player-artwork-color', color);
        else player.style.removeProperty('--player-artwork-color');
    }
    const observer = new MutationObserver(() => update());
    observer.observe(element, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
    element.addEventListener('load', update, true); element.addEventListener('error', update, true); update();
    return () => {
        disposed = true; observer.disconnect();
        element.removeEventListener('load', update, true); element.removeEventListener('error', update, true);
        player.style.removeProperty('--player-artwork-color');
    };
}
export async function thumbnailArtwork(bytes) {
    const url = URL.createObjectURL(new Blob([bytes])), image = new Image();
    try {
        image.src = url; await image.decode();
        if (!image.naturalWidth || !image.naturalHeight || image.naturalWidth * image.naturalHeight > 64 * 1024 * 1024) throw new Error('Artwork dimensions are unsupported.');
        const scale = Math.min(1, 320 / Math.max(image.naturalWidth, image.naturalHeight));
        const canvas = document.createElement('canvas'); canvas.width = Math.max(1, Math.round(image.naturalWidth * scale)); canvas.height = Math.max(1, Math.round(image.naturalHeight * scale));
        const context = canvas.getContext('2d'); context.fillStyle = '#121212'; context.fillRect(0, 0, canvas.width, canvas.height); context.drawImage(image, 0, 0, canvas.width, canvas.height);
        const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', .88));
        if (!blob || blob.size > 512 * 1024) throw new Error('Artwork thumbnail is too large.');
        return new Uint8Array(await blob.arrayBuffer());
    } finally { image.src = ''; URL.revokeObjectURL(url); }
}
