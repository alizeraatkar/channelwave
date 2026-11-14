# Third-party dependencies

Original ChannelWave code uses the MIT license in `../LICENSE`. Dependencies retain
their own licenses. Keep the original notices when distributing binaries. This is a development inventory, not a completed binary-license audit.

| Dependency | Pinned version/source | License / evidence |
| --- | --- | --- |
| TDLib and official tdweb sources | `c15d3f5a5de6e3ba5839822c451152e5e18bb700` | Boost Software License 1.0; full text in `TDLib-LICENSE.txt` |
| Bit.BlazorUI | 10.6.2 | MIT, confirmed in package `.nuspec`; source commit `b5b29969566abc205a35e720654a813f540c2de7` |
| Photino.Blazor | 4.0.13 | Apache-2.0, confirmed in package `.nuspec`; source commit `4164eedbe5f7cb46ae83fbc3bd1ed8c459620ec7` |
| .NET / ASP.NET Core / EF Core / MAUI | See `Directory.Packages.props` and lockfiles | Preserve Microsoft runtime/package notices in each distribution |
| Playwright | 1.64.0 | Test-only package and its downloaded browser notices |
| xUnit | 4.0.1 | Test-only; preserve package notices if distributing test binaries |
| LibVLC | System `libvlc.so.5` | Not vendored. Audit exact library, selected codec plugins and distribution obligations before packaging |
| OpenSSL | Browser/Android/Apple 3.5.8; native Linux distro package; Windows vcpkg lock | Preserve each distribution's exact license/version and platform advisory evidence. Browser build ships the 3.5.8 license. |
| localforage / lie / immediate | Locked in `tdweb-build/package-lock.json` | Production browser bundle inputs and notices accompany generated assets. |
| esbuild | 0.28.2, build-only | MIT; only build tooling, not included in the browser runtime. |

NuGet transitive graphs are in each platform lockfile; test dependencies are in the root `package-lock.json`. The tdweb wrapper uses `npm ci --ignore-scripts` with the maintained `tdweb-build/package-lock.json`, Emscripten 7.0.0 and checksum-verified OpenSSL 3.5.8. The digest-pinned SDK supplies Node 24. The wrapper uses native BroadcastChannel/crypto.randomUUID instead of the old broadcast-channel/uuid dependency graph. `bundle-inputs.json` identifies bundled inputs, `dependency-lock.json` records the npm graph, and the emitted manifest records payload SHA256 checksums and tool versions. Source checksums and method signatures remain checked by `scripts/verify-td-schema.mjs`. Full native/system SBOM and binary-license review remains a release gate.

Historical upstream builds reported 53 graph findings in October and 51 on 2026-11-13, including 9 critical entries. The replacement graph's current audit reports zero advisories. This statement applies to the new recipe and its verified generated artifacts, not old distributed engines. Authenticated/device behavior and platform components beyond npm/NuGet require independent review. See `../docs/security-audit.md`.
