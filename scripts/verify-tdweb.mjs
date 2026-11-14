import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export async function verifyTdweb(folder) {
    const pin = JSON.parse(await readFile(new URL('../vendor/tdlib.lock.json', import.meta.url)));
    const manifest = JSON.parse(await readFile(path.join(folder, 'channelwave-build.json')));
    if (manifest.revision !== pin.revision || manifest.wrapperPatch !== pin.wrapperPatch || manifest.emscripten !== pin.emscripten
        || manifest.openssl !== pin.browserOpenSsl || !manifest.node?.startsWith(`${pin.browserNodeMajor}.`))
        throw new Error('The browser engine does not match the audited security build.');
    for (const name of ['tdweb.js', 'tdweb.worker.js', 'td_wasm.wasm']) {
        const hash = createHash('sha256').update(await readFile(path.join(folder, name))).digest('hex');
        if (hash !== manifest.sha256?.[name]) throw new Error(`Browser engine checksum mismatch: ${name}`);
    }
    const expectedLock = JSON.parse(await readFile(new URL('../vendor/tdweb-build/package-lock.json', import.meta.url)));
    const bundledLock = JSON.parse(await readFile(path.join(folder, 'dependency-lock.json')));
    if (JSON.stringify(bundledLock) !== JSON.stringify(expectedLock))
        throw new Error('Browser dependency lock differs from the audited build lock. Rebuild the engine.');
    const graph = JSON.parse(await readFile(path.join(folder, 'bundle-inputs.json')));
    for (const bundle of [graph.main, graph.worker]) {
        if (!bundle?.inputs || !bundle?.outputs) throw new Error('Missing bundle reachability evidence.');
        for (const file of Object.keys(bundle.inputs)) {
            if (/node_modules\/(?!localforage\/|lie\/|immediate\/)/.test(file.replaceAll('\\', '/')))
                throw new Error('Unexpected dependency in the production browser bundle.');
        }
        if (Object.values(bundle.outputs).some(output => output.imports?.some(imported => imported.external)))
            throw new Error('The browser bundle must not load external code.');
    }
    return manifest;
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    await verifyTdweb(path.resolve(process.argv[2] ?? 'src/ChannelWave.Host.Web/wwwroot/tdweb'));
    console.log('Verified browser engine provenance, payload checksums and bundled dependencies.');
}
