# Label Maker

An Emby plugin (in progress) that watches your download folders, matches new albums on MusicBrainz, writes clean tags, renames files with your Picard naming script and moves each album to the right library folder. Review work goes to the Embidextrous phone app.

**Status:** design and Phase 0 are done. The engine (`src/LabelMaker.Core`) matches albums on MusicBrainz (85% of a real library applied automatically with none wrong at the default settings), tags, renames and files them with undo and crash recovery, and has the inbox watcher and queue; all tested offline (197 tests). The Emby plugin shell, the settings page and the review screen are not built yet. See `docs/HANDOFF.md`, and `docs/MATCHING-RULES.md` for every rule in plain English.

## Layout
- `src/LabelMaker.Core` the engine. No Emby types.
- `tests/LabelMaker.Tests` unit tests (offline; MusicBrainz answers come from a recorded fixture).
- `docs/` design, Phase 0 results, handoff, reference files.

## Build and test
```
dotnet test tests/LabelMaker.Tests
```

## Third-party code
TagLibSharp (LGPL-2.1) is used for tags. See `THIRD-PARTY-NOTICES.md`.

Licence: MIT (see `LICENSE`).
