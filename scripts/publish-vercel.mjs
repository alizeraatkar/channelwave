// Build locally with the pinned .NET SDK, then upload only .vercel/output.
import { spawnSync } from 'node:child_process';
import { cp, mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { securityHeaders } from './web-security.mjs';
import { verifyTdweb } from './verify-tdweb.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const webRoot = path.join(root, 'src/ChannelWave.Host.Web/wwwroot');
const output = path.join(root, '.vercel/output');
const published = path.join(root, 'artifacts/vercel-publish');
const includeConfig = process.argv.includes('--include-local-config');
const pin = JSON.parse(await readFile(path.join(root, 'vendor/tdlib.lock.json')));
const runtime = await verifyTdweb(path.join(webRoot, 'tdweb'));
if (runtime.revision !== pin.revision || runtime.wrapperPatch !== 2)
    throw new Error('Build the pinned tdweb runtime with scripts/build-tdweb.ps1 first.');
await stat(path.join(webRoot, 'tdweb/tdweb.js'));
const distribution = await readdir(path.join(webRoot, 'tdweb'));
if (!distribution.some(name => name.endsWith('.wasm')) || !distribution.some(name => name.endsWith('.worker.js')))
    throw new Error('The pinned tdweb distribution is incomplete.');
function dotnet(args) {
    const result = spawnSync('dotnet', args, { cwd: root, stdio: 'inherit', shell: false });
    if (result.error) throw result.error;
    if (result.status !== 0) throw new Error(`dotnet ${args[0]} failed (${result.status}).`);
}
await rm(published, { recursive: true, force: true });
dotnet(['restore', 'src/ChannelWave.Host.Web', '--locked-mode']);
dotnet(['publish', 'src/ChannelWave.Host.Web', '-c', 'Release', '--no-restore', '-o', published]);
// This directory is generated and never contains application source or account data.
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
const staticRoot = path.join(output, 'static');
await cp(path.join(published, 'wwwroot'), staticRoot, { recursive: true });
await rm(path.join(staticRoot, 'channelwave.local.json'), { force: true });
// Config is never part of the public offline shell. Deployment inclusion is explicit.
if (includeConfig) {
    const { apiId, apiHash } = JSON.parse(await readFile(path.join(webRoot, 'channelwave.local.json')));
    if (!Number.isSafeInteger(apiId) || apiId <= 0 || !/^[a-f\d]{32}$/i.test(apiHash))
        throw new Error('Valid Telegram application API credentials are required.');
    await writeFile(path.join(staticRoot, 'channelwave.local.json'), JSON.stringify({ apiId, apiHash }) + '\n');
}
const config = {
    version: 3,
    routes: [
        { src: '/(.*)', headers: { 'Cache-Control': 'no-cache', ...securityHeaders }, continue: true },
        { src: '/audio-worker.js', headers: { 'Service-Worker-Allowed': '/' }, continue: true },
        { src: '/channelwave.local.json', headers: { 'Cache-Control': 'no-store' }, continue: true },
        { handle: 'filesystem' },
        { src: '/[^.]*', dest: '/index.html' }
    ],
    overrides: { 'manifest.webmanifest': { contentType: 'application/manifest+json' } }
};
await writeFile(path.join(output, 'config.json'), JSON.stringify(config, null, 2) + '\n');
const guard = spawnSync('python', ['scripts/security-check.py', 'artifacts', staticRoot,
    ...(includeConfig ? ['--allow-web-config'] : [])], { cwd: root, stdio: 'inherit', shell: false });
if (guard.error) throw guard.error;
if (guard.status !== 0) throw new Error('Deployment artifact security check failed.');
console.log('Vercel static output ready. Deploy with: npx vercel deploy --prebuilt --prod');
