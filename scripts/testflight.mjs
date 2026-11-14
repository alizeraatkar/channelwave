import { createPrivateKey, sign } from 'node:crypto';
import { readFile } from 'node:fs/promises';
const { ASC_KEY_ID: keyId, ASC_ISSUER_ID: issuer, ASC_APP_ID: app, ASC_INTERNAL_GROUP_ID: group,
    BASE_VERSION: version, RELEASE_BUILD_NUMBER: build } = process.env;
if (![keyId, issuer, app, group, version, build].every(Boolean)) throw new Error('Missing TestFlight configuration');
const key = createPrivateKey(await readFile(`${process.env.RUNNER_TEMP}/channelwave-apple/AuthKey.p8`));
async function api(path, method = 'GET', body) {
    const now = Math.floor(Date.now() / 1000);
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    const unsigned = `${encode({ alg: 'ES256', kid: keyId, typ: 'JWT' })}.${encode({ iss: issuer, iat: now, exp: now + 600, aud: 'appstoreconnect-v1' })}`;
    const jwt = `${unsigned}.${sign('sha256', Buffer.from(unsigned), { key, dsaEncoding: 'ieee-p1363' }).toString('base64url')}`;
    const response = await fetch(`https://api.appstoreconnect.apple.com/v1/${path}`, { method,
        headers: { Authorization: `Bearer ${jwt}`, 'Content-Type': 'application/json' }, body: body && JSON.stringify(body), signal: AbortSignal.timeout(30000) });
    if (!response.ok) throw new Error(`App Store Connect ${method}: HTTP ${response.status}`);
    return response.status === 204 ? null : response.json();
}
async function lookup() {
    const query = new URLSearchParams({ 'filter[app]': app, 'filter[version]': build, include: 'preReleaseVersion' });
    const result = await api(`builds?${query}`);
    if (result.data.length > 1) throw new Error('Ambiguous TestFlight build');
    const value = result.data[0];
    if (value) {
        const prerelease = result.included?.find(item => item.id === value.relationships.preReleaseVersion.data.id);
        if (prerelease?.attributes.version !== version) throw new Error('TestFlight build number belongs to another version');
    }
    return value;
}
if (process.argv[2] === 'lookup') {
    process.exitCode = await lookup() ? 0 : 3;
} else {
    let value;
    for (let attempt = 0; attempt < 180; attempt++) {
        value = await lookup();
        if (['FAILED', 'INVALID'].includes(value?.attributes.processingState)) throw new Error('Apple rejected processing');
        if (value?.attributes.processingState === 'VALID') break;
        await new Promise(resolve => setTimeout(resolve, 10000));
    }
    if (value?.attributes.processingState !== 'VALID') throw new Error('Timed out waiting for Apple processing');
    const audience = await api(`betaGroups/${encodeURIComponent(group)}`);
    if (!audience.data.attributes.isInternalGroup) throw new Error('Configured TestFlight group is not internal');
    await api(`betaGroups/${encodeURIComponent(group)}/relationships/builds`, 'POST', { data: [{ type: 'builds', id: value.id }] });
    let testing;
    for (let attempt = 0; attempt < 60; attempt++) {
        testing = (await api(`builds/${value.id}/buildBetaDetail`)).data.attributes.internalBuildState;
        if (testing === 'IN_BETA_TESTING') break;
        if (['MISSING_EXPORT_COMPLIANCE', 'IN_EXPORT_COMPLIANCE_REVIEW', 'EXPIRED', 'PROCESSING_EXCEPTION'].includes(testing))
            throw new Error(`TestFlight is not available to internal testers: ${testing}`);
        await new Promise(resolve => setTimeout(resolve, 10000));
    }
    if (testing !== 'IN_BETA_TESTING') throw new Error('Timed out waiting for internal TestFlight availability');
    process.stdout.write(JSON.stringify({ version, build, buildId: value.id, audience: 'internal', processing: 'VALID', testing }, null, 2) + '\n');
}
