// Local-only verification server for the actual published files and production headers.
import http from 'node:http';
import path from 'node:path';
import { readFile } from 'node:fs/promises';
import { securityHeaders } from './web-security.mjs';

const root = path.resolve(process.argv[2] ?? '.vercel/output/static');
const port = Number(process.argv[3] ?? 5196);
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.json': 'application/json',
    '.css': 'text/css', '.wasm': 'application/wasm', '.webmanifest': 'application/manifest+json',
    '.svg': 'image/svg+xml', '.png': 'image/png', '.woff2': 'font/woff2' };
http.createServer(async (request, response) => {
    try {
        if (!['GET', 'HEAD'].includes(request.method)) { response.writeHead(405).end(); return; }
        const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
        // Real loopback responses give Chromium a known local address space.
        // Fulfilled browser routes have an unknown/public space and can trigger
        // local-network blocking before CSP. These fixtures are test-server-only.
        if (pathname === '/__security_parent' && request.headers.host === `localhost:${port}`) {
            response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
            response.end(`<iframe id="control" src="http://127.0.0.1:${port}/__security_frame_control"></iframe><iframe id="app" src="http://127.0.0.1:${port}/"></iframe>`);
            return;
        }
        if (pathname === '/__security_frame_control') {
            response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
            response.end('<p>frame control</p>'); return;
        }
        let file = path.resolve(root, '.' + pathname);
        if (!file.startsWith(root + path.sep) && file !== root) { response.writeHead(403).end(); return; }
        if (!path.extname(file)) file = path.join(root, 'index.html');
        const bytes = await readFile(file);
        response.writeHead(200, { ...securityHeaders, 'Cache-Control': 'no-store',
            'Content-Type': types[path.extname(file)] ?? 'application/octet-stream' });
        response.end(request.method === 'HEAD' ? undefined : bytes);
    } catch { response.writeHead(404).end(); }
}).listen(port, '127.0.0.1', () => console.log(`Security verification server: http://127.0.0.1:${port}`));
