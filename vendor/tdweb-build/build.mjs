import { build } from 'esbuild';
import { readFile, writeFile, cp, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';

const root = process.cwd();
const source = path.join(root, 'src');
const output = path.join(root, 'dist');
await mkdir(output, { recursive: true });
// Emscripten 7 exports the filesystem implementations on Module, while the
// pinned wrapper uses the former FS.filesystems namespace. Preserve its on-disk
// IndexedDB layout and file operations through this narrow compatibility shim.
const workerSource = path.join(source, 'worker.js');
const workerText = await readFile(workerSource, 'utf8');
if (!workerText.includes('onFS(module.FS);')) throw new Error('Pinned filesystem initialization drift.');
await writeFile(workerSource, workerText.replace('onFS(module.FS);',
    'module.FS.filesystems = { IDBFS: module.IDBFS, WORKERFS: module.WORKERFS };\n  onFS(module.FS);'));
const options = { bundle: true, platform: 'browser', target: 'es2022', format: 'iife',
    legalComments: 'linked', metafile: true, logLevel: 'info', minify: false };
const main = await build({ ...options, entryPoints: [path.join(source, 'index.js')], outfile: path.join(output, 'tdweb.js'), globalName: 'tdweb' });
const worker = await build({ ...options, entryPoints: [path.join(source, 'worker.js')], outfile: path.join(output, 'tdweb.worker.js') });
await cp(path.join(source, 'prebuilt/release/td_wasm.wasm'), path.join(output, 'td_wasm.wasm'));
await writeFile(path.join(output, 'bundle-inputs.json'), JSON.stringify({ main: main.metafile, worker: worker.metafile }, null, 2) + '\n');
await cp('package-lock.json', path.join(output, 'dependency-lock.json'));
const revision = process.env.TDLIB_REVISION;
if (!/^[a-f0-9]{40}$/.test(revision ?? '')) throw new Error('TDLIB_REVISION is required.');
const files = ['tdweb.js', 'tdweb.worker.js', 'td_wasm.wasm'];
const sha256 = Object.fromEntries(await Promise.all(files.map(async file => [file, createHash('sha256').update(await readFile(path.join(output, file))).digest('hex')])));
await writeFile(path.join(output, 'channelwave-build.json'), JSON.stringify({ revision, wrapperPatch: 2, emscripten: '7.0.0', openssl: '3.5.8', node: process.versions.node, sha256 }, null, 2) + '\n');
