# STATUS: Label Maker (label-maker-emby)

Last updated: 2026-11-03. Master state document: `docs/HANDOFF.md` section 0 (rewritten after every release).

## What this is
An Emby plugin that watches music inbox folders, matches albums on MusicBrainz, tags files like Picard, renames them with Picard's stock script, files them into the library with undo, and fetches missing cover art. Dry run is on by default. GitHub `feerlessleadr/label-maker-emby` (public, MIT).

## Current state
- Latest release `v0.1.11.0` (2026-11-03, tag on `9c333ab`): status websocket channel plus a `Status.Version` fix. `main` equals `origin/main` at `1786919` ("Rewrite the handoff snapshot for 0.1.11.0 and record every decision"). Working tree clean.
- 363 unit tests and 70 live checks pass (live test starts its own private Emby on port 8196).
- Calibration on 287 real albums: 82% of full albums filed automatically (98% on the right album), 86% of single tracks (99%).
- The owner installed it on the production server. On 2026-10-19 they reported dry run found the right match for 1 single and 1 album. Full trial results are not in.

## In progress
Nothing. Everything on the roadmap is built.

## Next steps
1. Fix whatever the owner's production trial shows. Reproduce on a private instance first (`-KeepRunning`).
2. The owner tells the Embidextrous session that `docs/NOTIFICATIONS.md` sections 12 and 13 exist. The app side (Library tab, status channel) is not built in the app yet. Only notification catalog entries and samples exist there.
3. Release 1.0 after a successful trial and the owner saying "publish it".

## Known issues and traps
See HANDOFF section 0 "Traps learned the hard way". The biggest: a Windows UAC prompt may block the live test (trap 13). Never use `perl -0pi` with `|` in patterns (it broke 0.1.1.0).

## Not verified in the field
- Everything from 0.1.6.0 on against the real server and library: cover download from the real Cover Art Archive, keep both and replace, later tracks in a filed folder, song-title suggestions, classical, audiobook and podcast hold.
- The Android app using either websocket channel.

## Connections (details in WORKSPACE.md)
- Embidextrous app subscribes with `LabelMakerEventsStart` and `LabelMakerStatusStart` (admin token in the message body). This repo owns the plugin side (`docs/NOTIFICATIONS.md`, `docs/API.md`). The shared event shape is in `C:\dev\embidextrous\docs\push-contract.md`.
- MusicGrabber (Docker, three instances) writes the inbox folders. MusicBrainz and the Cover Art Archive are the only outside services.
- Hall Pass (`C:\dev\hall-pass-emby`) is the template repo. It tags files after Label Maker files them into the library.

## Owner to-dos (manual)
Update to the latest release and keep dry run on. Work one lane at a time. Report what Ready and review lists show. Approve any UAC prompt when a session runs the live test.
