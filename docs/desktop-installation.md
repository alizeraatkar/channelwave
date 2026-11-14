# Desktop beta installation

Windows x64: extract the complete portable ZIP into a user-writable directory and
run ChannelWave.Host.Maui.exe. The .NET and Windows App SDK runtimes are included.
Microsoft Edge WebView2 Evergreen Runtime is required. This beta is unsigned;
Windows may show an unknown-publisher prompt. Verify SHA256SUMS before use.

Linux x64: Ubuntu 24.04 is the supported packaging baseline. Install the .deb with
`sudo apt install ./ChannelWave-*-linux-x64.deb`; apt resolves GTK, WebKitGTK, VLC,
libnotify, .NET OS libraries, OpenSSL, zlib, Secret Service tools and D-Bus dependencies. Unlock a desktop keyring
before connecting. The tar archive requires those same dependencies. Run the
extracted ChannelWave.Host.Linux executable from a normal graphical desktop session.

macOS ARM64: once enabled, mount the signed and notarized DMG and copy ChannelWave
to Applications. Intel Macs are not included in this initial beta matrix.

Release packages include application registration credentials, as Android does.
These identify ChannelWave and are extractable; they are not Telegram account
credentials. A user-provided channelwave.local.json in ChannelWave's application
data directory takes precedence. Sessions and encryption keys remain local in
platform secure storage (Secret Service on Linux). Never share that directory.

Use a previous release's verified package to roll back application binaries.
Keep the same application ID and Android signing key. Never delete user data as
part of upgrade/rollback; a future incompatible data migration requires its own
backup and rollback procedure.
