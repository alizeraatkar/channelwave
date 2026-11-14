import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { verifyTdweb } from '../../scripts/verify-tdweb.mjs';

test('browser provenance rejects altered payloads, unexpected runtime inputs and obsolete toolchains', async () => {
    const root = await mkdtemp(path.join(tmpdir(), 'channelwave-tdweb-test-'));
    const pin = JSON.parse(await readFile(new URL('../../vendor/tdlib.lock.json', import.meta.url)));
    try {
        const files = ['tdweb.js', 'tdweb.worker.js', 'td_wasm.wasm'];
        for (const file of files) await writeFile(path.join(root, file), 'synthetic payload');
        const manifest = { revision: pin.revision, wrapperPatch: pin.wrapperPatch, emscripten: pin.emscripten,
            openssl: pin.browserOpenSsl, node: '24.19.0', sha256: Object.fromEntries(files.map(file => [file, createHash('sha256').update('synthetic payload').digest('hex')])) };
        const bundle = { inputs: { 'node_modules/localforage/dist/localforage.js': {} }, outputs: { 'dist/output.js': { imports: [] } } };
        await writeFile(path.join(root, 'channelwave-build.json'), JSON.stringify(manifest));
        const dependencyLock = await readFile(new URL('../../vendor/tdweb-build/package-lock.json', import.meta.url));
        await writeFile(path.join(root, 'dependency-lock.json'), dependencyLock);
        await writeFile(path.join(root, 'bundle-inputs.json'), JSON.stringify({ main: bundle, worker: bundle }));
        await verifyTdweb(root);
        await writeFile(path.join(root, 'dependency-lock.json'), '{}');
        await assert.rejects(verifyTdweb(root), /dependency lock differs/);
        await writeFile(path.join(root, 'dependency-lock.json'), dependencyLock);
        await writeFile(path.join(root, files[0]), 'altered payload');
        await assert.rejects(verifyTdweb(root), /checksum mismatch/);
        await writeFile(path.join(root, files[0]), 'synthetic payload');
        await writeFile(path.join(root, 'channelwave-build.json'), JSON.stringify({ ...manifest, openssl: '1.1.0l' }));
        await assert.rejects(verifyTdweb(root), /audited security build/);
        await writeFile(path.join(root, 'channelwave-build.json'), JSON.stringify(manifest));
        bundle.inputs['node_modules/unreviewed-package/index.js'] = {};
        await writeFile(path.join(root, 'bundle-inputs.json'), JSON.stringify({ main: bundle, worker: bundle }));
        await assert.rejects(verifyTdweb(root), /Unexpected dependency/);
    } finally { await rm(root, { recursive: true, force: true }); }
});
