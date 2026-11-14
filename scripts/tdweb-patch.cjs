// Compatibility/security overlay for the exact pinned sources. No protocol changes.
const fs = require('fs');
const base = '/td/example/web/tdweb/';
function replace(file, before, after) {
    const text = fs.readFileSync(base + file, 'utf8');
    if (!text.includes(before)) throw new Error('Pinned tdweb wrapper drift: ' + file);
    fs.writeFileSync(base + file, text.replace(before, after));
}
replace('src/index.js', "import MyWorker from './worker.js';", "class MyWorker extends Worker { constructor() { super('/tdweb/tdweb.worker.js'); } }");
replace('src/index.js', "import BroadcastChannel from 'broadcast-channel';", "class BrowserChannel { constructor(name) { this.channel = new globalThis.BroadcastChannel(name); this.channel.onmessage = event => this.onmessage?.(event.data); } postMessage(message) { this.channel.postMessage(message); } }");
replace('src/index.js', 'new BroadcastChannel(', 'new BrowserChannel(');
replace('src/index.js', "import uuid4 from 'uuid/v4';", 'const uuid4 = () => crypto.randomUUID();');
replace('src/worker.js', 'query.use_secret_chats = useDb;', 'query.use_secret_chats = false;');
replace('src/logger.js', 'return this.level >= level;', 'return false; // ChannelWave: never log Telegram payloads.');
replace('src/logger.js', 'checkVerbosity(level) {', 'checkVerbosity() {');
// The generated module owns WASM instantiation, without the old wrapper cache/logs.
const worker = fs.readFileSync(base + 'src/worker.js', 'utf8');
const begin = worker.indexOf('async function loadTdlibWasm(');
const end = worker.indexOf('async function loadTdlib(onFS, wasmUrl)', begin);
if (begin < 0 || end < 0) throw new Error('Pinned WASM loader drift.');
fs.writeFileSync(base + 'src/worker.js', worker.slice(0, begin)
    + `async function loadTdlibWasm(onFS) {
  const { default: createModule } = await import('./prebuilt/release/td_wasm.js');
  const module = await createModule({ locateFile: file => '/tdweb/' + file, print: () => {}, printErr: () => {} });
  onFS(module.FS);
  return module;
}
\n` + worker.slice(end));
let patched = fs.readFileSync(base + 'src/worker.js', 'utf8')
    .replace("import { instantiateAny } from './wasm-utils.js';", '')
    .replace("import td_wasm_release from './prebuilt/release/td_wasm.wasm';", '');
patched = patched.replace(/console\.log\([^;]*\);/g, '');
fs.writeFileSync(base + 'src/worker.js', patched);
const cmake = '/td/CMakeLists.txt';
let configuration = fs.readFileSync(cmake, 'utf8');
if (!configuration.includes('-s MEMFS_APPEND_TO_TYPED_ARRAYS=1') || !configuration.includes('--post-js ${CMAKE_CURRENT_SOURCE_DIR}/post.js')) throw new Error('Pinned Emscripten flags drift.');
configuration = configuration.replaceAll('-s MEMFS_APPEND_TO_TYPED_ARRAYS=1', '-s DYNAMIC_EXECUTION=0 -s ENVIRONMENT=worker -s EXPORT_ES6=1')
    .replace('--post-js ${CMAKE_CURRENT_SOURCE_DIR}/post.js', '');
fs.writeFileSync(cmake, configuration);
