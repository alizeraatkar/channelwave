import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const root = path.resolve(import.meta.dirname, '..');
const vendor = path.join(root, 'vendor');
const manifestPath = path.join(vendor, 'tdlib.lock.json');
const lock = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
const files = ['td_api.tl', 'tdweb-reference.js', 'tdweb-worker-reference.js', 'tdweb-webpack-reference.js', 'tdweb-logger-reference.js'];
const hashes = Object.fromEntries(files.map(file => [file, crypto.createHash('sha256').update(fs.readFileSync(path.join(vendor, file))).digest('hex')]));
const schema = fs.readFileSync(path.join(vendor, 'td_api.tl'), 'utf8').split(/\r?\n/);
const required = ['setTdlibParameters','addProxy','getOption','getAuthorizationState','setAuthenticationPhoneNumber','checkAuthenticationCode','checkAuthenticationPassword','setAuthenticationEmailAddress','checkAuthenticationEmailCode','resendAuthenticationCode','requestQrCodeAuthentication','getMe','loadChats','getChats','getChat','searchChatMessages','getMessage','getFileDownloadedPrefixSize', 'downloadFile','cancelDownloadFile','logOut','destroy','close'];
required.push('getUser', 'getMessageProperties', 'getMessageLink');
required.push('getMessages', 'openChat', 'closeChat', 'updateNewMessage', 'updateMessageContent', 'updateMessageEdited', 'updateDeleteMessages');
required.push('updateMessageSendSucceeded');
required.push('deleteFile', 'getStorageStatisticsFast', 'optimizeStorage');
const signatures = Object.fromEntries(required.map(name => {
    const line = schema.find(line => line.startsWith(name + ' '));
    if (!line) throw new Error(`Missing required TDLib method: ${name}`);
    return [name, line];
}));
const generated = JSON.stringify({ revision: lock.revision, schemaSha256: hashes['td_api.tl'], signatures }, null, 2) + '\n';
const contractPath = path.join(vendor, 'td-api-contract.json');
if (process.argv.includes('--write')) {
    lock.sha256 = hashes;
    fs.writeFileSync(manifestPath, JSON.stringify(lock, null, 2) + '\n');
    fs.writeFileSync(contractPath, generated);
} else {
    if (JSON.stringify(lock.sha256) !== JSON.stringify(hashes)) throw new Error('Vendored TDLib source checksum mismatch. Review before regenerating the lock.');
    if (fs.readFileSync(contractPath, 'utf8') !== generated) throw new Error('TDLib contract drift. Review and regenerate the signature contract.');
    const protocol = fs.readFileSync(path.join(root, 'src/ChannelWave.Telegram/Protocol.cs'), 'utf8');
    if (!protocol.includes(lock.revision)) throw new Error('C# protocol revision differs from the source lock.');
}
console.log(`Verified ${required.length} TDLib method signatures at ${lock.revision}.`);
