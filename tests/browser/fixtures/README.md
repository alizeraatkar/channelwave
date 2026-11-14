`stream-tone.mp3` is an original 30-second 440 Hz sine wave used to verify playback while later chunks are deliberately withheld. It contains no third-party music.

Regenerate with FFmpeg (the generated file is checked in; running tests does not require FFmpeg):

```sh
ffmpeg -f lavfi -i 'sine=frequency=440:duration=30' -c:a libmp3lame -b:a 96k -ac 1 -map_metadata -1 stream-tone.mp3
```

Other browser audio fixtures generate an original five-second PCM WAV in memory. WAV metadata may require more of the file before playback starts, so the incremental-start test uses MP3.

`telegram.mjs` supplies the shared synthetic Telegram transport and responsive navigation helpers. Adapter regression tests and the desktop/mobile design journey use this fixture; it is never imported by production code. Design screenshots are written to `artifacts/browser-review/` and contain synthetic channel/music metadata.

`offline.spec.mjs` uses the synthetic PCM fixture with real IndexedDB and service-worker caching. Desktop/mobile tests disable browser-context networking before reload and assert playback makes no Telegram calls; no real account is used.
