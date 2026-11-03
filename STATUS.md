# STATUS: Label Maker (label-maker-emby)

Last updated: 2026-11-03. Master state document: `docs/HANDOFF.md` section 0 (rewritten after every release). Decisions with reasons: `DECISIONS.md` here (search it) and the dated log in HANDOFF sections 11 to 37.

## What this is
An Emby plugin that watches music inbox folders, matches albums on MusicBrainz, tags files like Picard, renames them with Picard's stock script, files them into the library with undo, and fetches missing cover art. Dry run is on by default. GitHub `feerlessleadr/label-maker-emby` (public, MIT).

## Current state
- Latest release `v0.1.11.0` (2026-11-03, tag on `9c333ab`): the status websocket channel for the app plus a `Status.Version` fix. Releases since the first trial report: 0.1.6.0 (Ready albums filed when dry run is turned off), 0.1.7.0 (cover art), 0.1.8.0 (keep both / replace, later tracks of a filed folder are filed), 0.1.9.0 (song-title search, file only the new files), 0.1.10.0 (classical, audiobook and podcast held for review), 0.1.11.0.
- `main` equals `origin/main` at `ede13db` (docs commit).
- 363 unit tests and 70 live checks pass (the live test starts its own private Emby on port 8196). Page files are unchanged since 0.1.9.0.
- Calibration on 287 real albums: 82% of full albums filed automatically (98% on the right album), 86% of single tracks (99%). Song-title search on 40 albums with the name removed: all 40 reach review, the right album first for 30 (the 10 misses are single songs on many albums).
- The owner installed it on the production server. On 2026-10-19 they reported dry run found the right match for 1 single and 1 album, and found one bug (fixed in 0.1.6.0). Full trial results are not in.

## Done this session (2026-10-19 to 2026-11-03)
Released 0.1.6.0 to 0.1.11.0 (see Current state), each on the owner's "publish it", each with the published DLL checked (SHA-256, live test). Fixed two serious bugs on the way: Ready albums not filed after dry run was turned off, and tracks arriving later in an already filed folder never being filed. Rewrote HANDOFF section 0 as a full snapshot, backfilled `DECISIONS.md`, added the workspace entries, committed and pushed (`ede13db`).

## In progress
Nothing. Everything on the roadmap is built and released. No leftover test processes or folders. Working tree clean, `main` equals `origin/main` at `ede13db`.

## Very next action
Ask the owner how the production trial is going and what the Ready and review lists show. Do not start new features unasked.

## Next steps
1. Fix whatever the owner's production trial shows. Reproduce on a private instance first (`-KeepRunning`), by the owner's path (dashboard sidebar > Advanced > Label Maker).
2. The owner tells the Embidextrous session that `docs/NOTIFICATIONS.md` sections 12 and 13 exist. The app's Library tab (and its use of the status channel) is not built yet.
3. Release 1.0 after a successful trial and the owner saying "publish it".
4. Ideas nobody asked for yet (offer, do not start): better suggestions for a single song on many albums; a setting for the classical/audiobook safety net; embedding the cover in the tags; a release-group fallback for covers; comparing quality, not only file size, when offering "replace".

## Open questions
- None blocking. Whether genre-based recognition of classical music and audiobooks is too eager is unknown until the owner re-exports their library (`tools/Export-LibraryTags.ps1` now writes genre) and someone runs `tools/LabelMaker.Calibrate --media-only`.

## Known issues and traps
See HANDOFF section 0 "Traps learned the hard way". The biggest: a Windows UAC prompt may block the live test unless the owner approves it at the machine within about three minutes (trap 13); the browser pane caches old page scripts (trap 15); MusicBrainz sometimes answers "busy" (trap 8). Never use `perl -0pi` with `|` in patterns (it broke 0.1.1.0).

## Not verified in the field
- Everything from 0.1.6.0 on against the real server and library: cover download from the real Cover Art Archive, keep both and replace and file-only-new, later tracks in a filed folder, song-title suggestions, classical, audiobook and podcast hold, genre-based recognition.
- The Android app using either websocket channel.

## Connections (details in WORKSPACE.md)
- Embidextrous subscribes with `LabelMakerEventsStart` (`"0,30000,<afterSeq>,<token>"`) and `LabelMakerStatusStart` (`"0,30000,<token>"`). This repo owns the plugin side (`docs/NOTIFICATIONS.md`, `docs/API.md`). The shared event shape is in `C:\dev\embidextrous\docs\push-contract.md`. Never edit the app from here.
- MusicGrabber (three instances) writes the inbox folders. MusicBrainz and the Cover Art Archive are the only outside services.
- Hall Pass (`C:\dev\hall-pass-emby`) is the template repo. It tags files after Label Maker files them into the library.

## Owner to-dos (manual)
Update to the latest release and keep dry run on. Work one lane at a time. Report what the Ready and review lists show, and anything odd with covers, duplicates, later tracks, or classical and spoken-word folders. Tell the Embidextrous session about the status channel. Approve the Windows admin prompt when a session runs the live test. Optional: re-run the library export for genres.
