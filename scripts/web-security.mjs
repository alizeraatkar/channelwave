// Telegram destinations are the five production DC hosts in the pinned
// ConnectionCreator.cpp. No arbitrary HTTPS/WebSocket destinations are permitted.
export const contentSecurityPolicy = [
    "default-src 'self'",
    "base-uri 'self'",
    "object-src 'none'",
    "frame-ancestors 'none'",
    "frame-src 'none'",
    "form-action 'none'",
    "script-src 'self' 'wasm-unsafe-eval'",
    "worker-src 'self' blob:",
    "connect-src 'self' wss://pluto.web.telegram.org wss://venus.web.telegram.org wss://aurora.web.telegram.org wss://vesta.web.telegram.org wss://flora.web.telegram.org",
    "img-src 'self' data: blob:",
    "media-src 'self' blob:",
    "font-src 'self' data:",
    // Blazor/Bit components use dynamic style attributes; scripts remain restricted.
    "style-src 'self' 'unsafe-inline'"
].join('; ');

export const securityHeaders = {
    'Content-Security-Policy': contentSecurityPolicy,
    'X-Frame-Options': 'DENY',
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'no-referrer',
    'Strict-Transport-Security': 'max-age=63072000; includeSubDomains; preload',
    'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), speaker-selection=(self)'
};
