# Architecture: shared C# and Blazor clients

ChannelWave uses .NET 10, framework-independent domain logic and a shared Razor
UI with Bit.BlazorUI. MAUI hosts Android, iOS, Windows and Mac Catalyst;
Photino.Blazor hosts Linux. Standalone Blazor WebAssembly runs in the browser.
There is no backend owning Telegram sessions or music data.

## Dependencies and platform boundaries

Hosts compose UI and adapters. Core contracts have no MAUI, Razor, EF Core or
JavaScript dependency. Telegram protocol types stay outside the product model.
Native hosts load TDLib through its JSON C interface; Apple AOT statically links
the archive through `__Internal`. The browser uses the official tdweb worker
and WASM source. Both reject authentication when the runtime commit differs
from the pinned schema. No Bot API or scraping is used.

Native metadata uses SQLite/EF Core; browser metadata uses IndexedDB. Platform
adapters provide audio, output routing, vaults, storage, files and native links.
Native audio prepares complete files; the browser streams bounded ranges through
a service worker. Cache, transfer and playback coordination share core behavior.

## Data and lifecycle

The application currently uses one Telegram account per installation/profile.
Stores preserve `(account, chat, message)` identity, local GUIDs and favorites
across indexing. Cursor and page writes commit atomically. File IDs are resolved
again before transfers; completion follows TDLib state and host byte validation.
Playback generations reject stale selections and downloads.

Only selected sources are indexed. Discovery, reconciliation, artwork and metadata
work run during the application lifetime and resume from saved progress.
Queue and listening restoration is paused until explicit Resume; a live native
service survives activity recreation without starting a second player.

Native vaults protect session keys. Browser data relies on origin isolation.
Native metadata and audio rely on OS file access controls. Sign-out records durable
cleanup intent, closes the session and erases account data; partial failures block
normal restoration until recovery succeeds. See [privacy](privacy.md).

## Tradeoffs and release requirements

The shared C#/Razor stack requires native audio services, browser interop, two
storage adapters and a separate Linux host. Bit's server boilerplate and browser
SQLite are unnecessary for the local-first design. Flutter and Tauri were
considered; the current stack keeps product logic in C# across all hosts.

Cross-device synchronization, server identity, subscriptions and telemetry are
outside the current product. Platform compilation does not prove device behavior.
Release requirements include Telegram client eligibility and sponsored-message
review, dependency notices/SBOMs, secure WebView/origin configuration, signing,
data-preserving upgrades, authenticated flows and target-specific playback.
See [verification](verification.md), [security audit](security-audit.md) and
[development setup](development.md).
