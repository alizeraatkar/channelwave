# Vercel PWA deployment

ChannelWave is a standalone Blazor WebAssembly app. Build with .NET SDK
10.0.302 and Node 24 locally, then deploy the static Build Output API package.
No .NET server is required on Vercel.

Production: [channelwave.vercel.app](https://channelwave.vercel.app).

1. Build the pinned browser runtime with `./scripts/build-tdweb.ps1` if
   `src/ChannelWave.Host.Web/wwwroot/tdweb` is absent.
2. Run `node scripts/publish-vercel.mjs` from the repository root.
3. Run `npx vercel login` and `npx vercel link --project channelwave` once.
4. Run `npx vercel deploy --prebuilt --prod`.

The publish script uses locked restore and Release publishing. It stages only
published web assets in ignored `.vercel/output/static`. Vercel project settings
and environment files remain ignored. Git integration is separate from this
local deployment workflow; Vercel's default build image does not supply the pinned
.NET SDK or the ignored tdweb distribution.

Local Telegram configuration is excluded from ordinary .NET publishing and the
default deployment package. To deliberately expose your application registration
in a browser deployment, run `node scripts/publish-vercel.mjs --include-local-config`.
Only validated `apiId` and `apiHash` fields are included. These values are publicly
inspectable, as with any browser Telegram client; never supply sessions or login
codes. Without this option, Telegram sign-in requires separately supplied
application configuration. No account database or saved audio is deployed.

If uploads time out, configure HTTP_PROXY and HTTPS_PROXY for the CLI process
using your network's proxy endpoint, then retry the same prebuilt package.

The manifest provides standalone installation, regular 192/512 pixel icons and a
512 pixel maskable icon. Install from the browser's install menu, or use Safari's
Share > Add to Home Screen. After a complete first online load, the existing
service worker caches the public shell and install assets for offline reopening.
Saved music remains in account-scoped browser storage. Updates wait for idle tabs
and preserve listening, downloads and unsaved editors.

Deployment routes revalidate static assets and worker manifests, serve existing
files before the extensionless navigation fallback, and give configuration
`no-store`. Missing `.wasm`, JavaScript or JSON files retain a 404 response.
Validate the live manifest, worker activation, WASM MIME types and offline reload
after deploying. Local browser tests use
`CHANNELWAVE_TEST_PUBLISHED=1` with a static server serving the Release output;
`tests/browser/pwa.spec.mjs` verifies install metadata and offline reopening.

The publisher requires security wrapper version 2, checks the engine/toolchain
manifest and payload hashes, and scans the staged output before accepting it.
It applies the enforced CSP, DENY framing, HSTS, nosniff, no-referrer and device
permission policy from `scripts/web-security.mjs`. Other static hosts must apply
equivalent response headers, including on worker scripts. Rebuild old tdweb assets;
do not permit JavaScript unsafe-eval/unsafe-inline to accommodate them.

For local production-policy verification, run `node scripts/serve-security-test.mjs`
after publishing, then set `CHANNELWAVE_TEST_EXTERNAL_SERVER=1`,
`CHANNELWAVE_TEST_PORT=5196`, `CHANNELWAVE_SECURITY_HEADERS=1`,
`CHANNELWAVE_TEST_PUBLISHED=1` and `CHANNELWAVE_VERIFY_TDWEB=1` for the browser
security suite. The verifier binds only loopback and contains control-frame routes
solely for testing; it is not a production hosting server. These local tests do
not change or validate an existing remote deployment.
