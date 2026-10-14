# Label Maker

An Emby Server plugin that watches your download folders, matches each new album on MusicBrainz, writes clean tags, names the files the way MusicBrainz Picard's stock script does, and files every album into the right library folder. It asks you only when it is not sure, and every change can be undone.

**Status: trial build (0.x).** It starts in **dry run**: it matches albums and shows what it would do, and moves nothing by itself. Read `docs/INSTALL-TRIAL.md` before turning dry run off.

## What it does
- Watches one or more **lanes** (an inbox folder and the library folder its albums go to). A folder is picked up once its files have stopped changing.
- Finds the album on MusicBrainz (the release id MusicGrabber saved, a release id already in the tags, or a search by artist and album) and checks **every file against the edition's track list**. Partial albums and single tracks are normal.
- Files an album by itself only when every file fits one edition clearly; otherwise it goes on a **review list** with a plain sentence saying why.
- Tags the files (ID3v2.4, UTF-8, ID3v1 kept, like Picard's defaults), renames them with Picard's stock naming script, and moves the cover and `.lrc` lyric files with them.
- **Undo** for every filed album; nothing is ever deleted (a discarded album goes to a `_Trash` folder inside its inbox).
- **Folder health check** (can it read, create, rename and delete here; free space; path length) and plain-English lane rules.
- **Phone alerts** over Emby itself to the Embidextrous app (no outside service): albums need review, an album failed, a lane has a problem, music was filed, lookups are stuck.

## Install
Install `LabelMaker.dll` from the latest GitHub release with the GitHub Repo Plugin Install plugin, or copy it into Emby's `plugins` folder and restart Emby. A new item **Label Maker** appears under **Advanced** in the dashboard. Step by step: `docs/INSTALL-TRIAL.md`.

The release also carries `LabelMaker.dll.sha256` and a signed build attestation (`gh attestation verify LabelMaker.dll --repo feerlessleadr/label-maker-emby`).

## What leaves your server
Only **artist, album and track names** (and counts) go to `musicbrainz.org`, one request a second, answers cached on disk. Nothing else is sent anywhere. Details: `SECURITY.md`.

## Documents
- `docs/MATCHING-RULES.md` every rule in plain English.
- `docs/API.md` the plugin's routes; `docs/NOTIFICATIONS.md` the phone alert contract.
- `docs/DESIGN.md` the design; `docs/HANDOFF.md` the state of the project and its history; `docs/CALIBRATION.md` how the thresholds were measured.

## Layout
- `src/LabelMaker.Core` the engine, no Emby types (matching, tagging, planning, filing, queue, notifications).
- `src/LabelMaker` the Emby plugin (one DLL: the engine compiled in, TagLibSharp embedded, settings page, routes).
- `tests/LabelMaker.Tests` unit tests (offline). `tools/` the calibration harness and the live plugin test.

## Build and test
```
dotnet test tests/LabelMaker.Tests
dotnet build src/LabelMaker -c Release
```
The live test against a local Emby server is described in `TESTING.md`.

## Third-party code
TagLibSharp (LGPL-2.1) is embedded unmodified. See `THIRD-PARTY-NOTICES.md`.

Licence: MIT (see `LICENSE`).
