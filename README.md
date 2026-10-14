# Label Maker

An Emby plugin (in progress) that watches your download folders, matches new albums on MusicBrainz, writes clean tags, renames files with your Picard naming script and moves each album to the right library folder. Review work goes to the Embidextrous phone app.

**Status:** design and Phase 0 are done. The engine (`src/LabelMaker.Core`) now has naming, tags, the MusicBrainz client, the album matcher, and the planner and applier (journal, resume, undo), all tested offline (102 tests). The inbox scanner, the Emby plugin shell and the review UI are not built yet. See `docs/HANDOFF.md`.

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
