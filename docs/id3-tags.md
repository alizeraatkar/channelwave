# MP3 tags and local library corrections

Use **Edit details** on a track to open two independent editors. **Save local corrections** changes this device's title and artist labels, including search and offline collections. **Revert to source** restores the latest indexed Telegram labels. Neither action changes the file or Telegram message. Corrections survive restart and reindexing, are scoped to the account and track, and are removed with the source or account.

**Read tags** reads a saved offline copy or explicitly downloads that one track. Indexing never reads file tags or downloads media for this feature. The file fields show embedded values; missing values remain empty. **Export tagged copy** writes a new MP3 using the host's existing download/share destination. The original TDLib file, browser offline Blob and Telegram message remain unchanged. File-tag drafts are discarded when the editor closes; exporting does not save local corrections.

## Compatibility

| Input | Reading | Export after editing |
| --- | --- | --- |
| MP3 without tags | Empty fields | Adds ID3v2.3 |
| ID3v1 / ID3v1.1 | Latin-1 text, standard/extended genre names and v1.1 track number | Adds Unicode ID3v2.3; synchronizes changed representable legacy fields |
| ID3v2.3 | Standard text/APIC frames, padding and tag unsynchronisation | Retains v2.3; changed text uses UTF-16 with BOM |
| ID3v2.4 | Standard text/APIC frames, padding, footer, tag/frame unsynchronisation and data length indicators | Retains v2.4; changed text uses UTF-8 |
| ID3v2.2, other versions/revisions, extended headers, experimental flags | Identified with an explanation; only safely decoded values are shown | Read-only |
| Compressed, encrypted, grouped, read-only or other unsupported frame flags | Safely decoded ordinary frames remain visible | Entire tag is read-only |
| Truncated/malformed tags, invalid encoding, inconsistent lengths or footer | Empty/partial safe values and an explanation | Read-only; no automatic repair |
| WAV, FLAC, Ogg, MP4 and other non-MP3 formats | MP3-only explanation | Unsupported |

MP3 validation requires two consecutive MPEG Layer III frames immediately after the leading tag. Free-format MP3 and files with leading junk are conservatively unsupported. Filename extensions and Telegram MIME labels alone are insufficient.

Editable fields are title (`TIT2`), artist (`TPE1`), album (`TALB`), year (`TYER`/`TDRC`), genre (`TCON`), track (`TRCK`) and disc (`TPOS`). Track/disc accept a positive number or `number/total`; both values are limited to 999999 and the number cannot exceed the total. Year accepts four digits from 0001 to 9999 or an empty value. Changing a full recording date changes its year frame to the entered year; untouched date frames retain their original bytes. Empty values remove the corresponding frames. Unchanged fields, multiple values and unknown frames retain their original representation.

Artwork can be selected, previewed, replaced or removed one picture at a time. Adding artwork creates a front-cover APIC frame; replacing a picture retains its type and description. Other pictures remain intact. Replacements must be JPEG/PNG, at most 10 MiB, and pass structural validation (including PNG chunk checksums). Unsupported embedded artwork remains preserved without a preview. Total leading tag data is limited to 32 MiB. New/changed text is limited to 10000 characters without control characters; local labels are limited to 1000.

Existing ID3v1 comments and unrelated bytes are preserved. Changed legacy fields that cannot represent the full value in Latin-1 or fit their fixed width are cleared, while the full value is stored in ID3v2. Existing v1.0 footers are not converted to v1.1, preserving their complete comments. Genre values without a legacy equivalent use the undefined genre byte.

Clearing the final field/picture removes the empty leading ID3v2 tag rather than writing a tag containing only padding. Audio and any preserved trailing metadata remain intact.

## Permissions, hosts and cancellation

Connected reads resolve the original message and verify account/saving permissions through the existing Telegram download service. Connected exports recheck permission. Existing offline copies can be reviewed/exported without reconnecting using cached permissions, as a deliberate product choice; any known protected status still blocks export. This cannot detect a Telegram permission change while disconnected.

The shared editor runs in browser, Linux and MAUI hosts. Browser audio stays in JavaScript Blobs; only bounded metadata ranges cross into .NET. Exports concatenate replacement tags and original Blob slices. Native hosts read file ranges and stream a separate copy to Linux Downloads or the MAUI share/save sheet. Closing/canceling the editor, changing accounts, removing its source or disposing the UI invalidates work and releases handles. Canceled/failed native copies are deleted before handoff. Once a download or OS share/save handoff has occurred, it is controlled by that browser or OS.

The feature adds optional override properties to existing track JSON; existing databases require no schema migration. It does not add album/year library filters, bulk editing or Telegram uploads. [F21](artwork.md) separately caches bounded source-artwork thumbnails for library/player display. Editing or exporting a picture does not replace that source artwork.

Specifications: [ID3v2.3](https://id3.org/id3v2.3.0), [ID3v2.4 structure](https://id3.org/id3v2.4.0-structure), [ID3v2.4 frames](https://id3.org/id3v2.4.0-frames), [ID3v1](https://id3.org/ID3v1).
