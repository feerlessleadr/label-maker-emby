# Label Maker: handoff (read this first in a new session)

Written 2026-10-13 at the end of Phase 0. Nothing is built yet except throw-away spikes. This file is meant to let a fresh session continue without the old chat. Then read `docs/PHASE0.md` (spike results) and `docs/DESIGN.md` (the full design and plan, 19 sections).

## 1. What this is
**Label Maker** = an Emby server plugin (C#, net6.0 target) that watches download folders, matches new music against MusicBrainz, writes clean tags and cover art, renames files with the owner's Picard naming script, moves them to the right library folder, and sends review work to the owner's phone app **Embidextrous** (Android, Kotlin/Compose, `C:\dev\embidextrous`). It replaces the owner's manual routine: log in to the server, open Picard, look up, save, move, and change Picard's output folder per person.

Related projects by the same owner (all `feerlessleadr` on GitHub, all with their own `docs/HANDOFF.md` or PROGRESS files):
- Hall Pass (explicit-music tagger plugin) `C:\dev\hall-pass-emby`, handoff `docs/HANDOFF.md`. **Best template for this plugin's structure** (Core library plus plugin shell, settings page, API, tests, CI, release workflow, docs).
- Embidextrous (the app) `C:\dev\embidextrous`, `docs/HANDOFF.md`, `docs/decisions.md`, `docs/plan.md`, `CLAUDE.md`.
- Hall Monitor (watch-time limits plugin) `C:\dev\watching-eye`, PROGRESS.md section 16.
- Older designs `C:\dev\emby-tagging` (Tag Team bulk tag editor design is there; not built; Label Maker's tag writer should become the shared writer).

Repo: https://github.com/feerlessleadr/label-maker-emby, local clone `C:\dev\label-maker-emby` (was empty; docs are written and committed locally; push only after the owner agrees, see section 9).

## 2. Owner environment (facts, not guesses)
- Production server: a **Windows 11 PC** that hosts Emby. Emby runs as the **tray app (embytray) under a Windows user account**, not as a service. Music library: one Emby library "Music" with four folders (main, kids, parents, shared; the shared one is on a different share root), per-user visibility set in Emby. The exact UNC paths are in the local-only `docs/local/ENVIRONMENT.md` (git-ignored). Library setting "Perfectly organized into artist\album folders, tracks directly in the album folders".
- Downloads come from **MusicGrabber** (https://gitlab.com/g33kphr33k/musicgrabber, Docker, three instances) writing to `E:\Downloads\Music`, `E:\Downloads\KidsMusic`, `E:\Downloads\SharedMusic`. A parents' inbox for the parents' library folder will be added later. Inboxes are outside Emby's libraries.
- This development PC is **not** the production server. It has a local test Emby (`<Downloads>\embyserver-win-x64-4.10.1.0`, http://localhost:8096, programdata in the same folder, test music library `C:\claude\Music`, 92 tracks) and no access to the shares. Real users on the test server (the existing users) must never be modified; use throw-away users and delete them.
- Test material: `C:\claude\musicgrabber-sample\` (real MusicGrabber output, see PHASE0 section 4), `C:\claude\picard-default-naming-script.txt` (copy in `docs/reference/`).
- Git identity for commits in this repo is already set locally: `feerlessleadr <github@thecgkg.com>`. Never use the work email or the Windows username in anything published.

## 3. Owner decisions (all confirmed in chat on 2026-10-13)
- Name **Label Maker**; plugin (not standalone), engine in an Emby-free `Core` library so it could be wrapped later.
- **Lanes** = inbox folder -> library folder, fully editable (add, edit, delete) on the plugin settings page; deleting a lane never touches files; initial lanes: `E:\Downloads\Music` -> `\\<server>\<main-share>`; `...\KidsMusic` -> `\\<server>\<kids-share>`; `...\SharedMusic` -> `\\<server>\<shared-share>`; later a parents' lane -> `\\<server>\<parents-share>`.
- Default output naming = Picard's stock script exactly (`docs/reference/picard-default-naming-script.txt`): `Artist/Album/01 Title`, disc number in the file name when more than one disc, no year folder, compilations `Various Artists/Album/01 Artist - Title`.
- **No "Singles" folder**: a single track is filed under the release MusicBrainz says it belongs to (Picard behaviour).
- **No match: leave in the inbox** and list it in review.
- **Dry run on by default** (like Hall Pass), undo and tag backup for every apply.
- **Notifications: Emby websocket push to the app's existing `AlertService`, polling only as a backup, no outside service** (no ntfy/Gotify/webhooks). Admin sessions only.
- **AcoustID fingerprinting deferred** (MusicGrabber already fingerprints). Text and MBID matching only in v1.
- Settings that change what is written or sent stay on the plugin's Emby page; the app shows status, review, lane list (read only for now) and quick switches (pause, dry run, check now).
- Wording and working rules: see section 8.

## 4. Phase 0 results in one screen (details in PHASE0.md)
1. **TagLibSharp 2.3.0** reads and writes MP3, FLAC, M4A, Opus and Ogg with Picard-compatible keys and preserves frames we do not own. A separate DLL beside the plugin does **not** load in Emby; **embed it as a resource and load it with `AssemblyLoadContext.Default.Resolving`** (working code in `docs/spikes/emby-probe-plugin/Probe.cs`; csproj shows the embedded resource). Always remove ID3v1/APE on MP3 save; ID3v2.3 via `Id3v2.Tag.DefaultVersion=3; ForceDefaultVersion=true`. LGPL notice needed.
2. **Emby APIs verified:** `ILibraryMonitor` (`ReportFileSystemChangeBeginning`/`Complete(path, true)`; new folders appear after ~90-120 s), `ILibraryManager.GetVirtualFolders()` (libraries and their paths), `ISessionManager.SendMessageToAdminSessions(name, data, ct)` (reaches administrator user sessions' websockets; an API-key-only socket gets nothing), `INotificationManager` exists. Plugins have no `Assembly.Location`.
3. **MusicBrainz matching strategy (tested):** release-group search -> browse that group's `status=official` releases with `inc=recordings` -> verify the files' titles/track numbers; check several release groups with the same title; recording search only as a fallback. Official releases only. **Partial albums are the normal case** (1-6 files of 9-30 track albums); MusicBrainz returns 503 under bursts, so queue, back off, honour `Retry-After`, cache.
4. **MusicGrabber's `.albuminfo` is JSON with `release_mbid`**: use it as a direct, high-confidence hint (look up the release by id and verify). Its files carry no MusicBrainz IDs, mix FLAC/M4A/MP3 within one album, place two discs flat in one folder (`01-01 - Title.ext`), write `.lrc` sidecars, `cover.jpg` + `folder.jpg`, embedded art, ReplayGain, ISRC, and custom tags (`SOURCE*`, `MUSICGRABBER_FILE_ID`/`FILE_ID`) that must be preserved. Its `date` tag is the pressing's year (2021 for Green Day's Insomniac, original 1995).
5. Spike scripts: `docs/spikes/ws-test.ps1` (creates a throw-away admin, opens an authenticated websocket, triggers a push, deletes the user), `docs/spikes/deploy-probe.ps1` (deploy the probe plugin to the test server and call it; paths inside are for the old scratchpad, adjust).

## 5. Planned architecture (summary; DESIGN.md has the details)
```
label-maker-emby/
  src/LabelMaker.Core/   no Emby references: models, TagReader/TagWriter (TagLibSharp), MusicBrainz client (queue, cache, 503 back-off),
                         normaliser, matcher/scorer + assignment solver, naming-script engine (Picard subset), sanitiser, planner,
                         applier (backup tags -> write -> verify -> cross-volume move -> clean inbox), journal/undo, state store
  src/LabelMaker/        Emby shell: Plugin (BasePlugin + IServerEntryPoint, embedded TagLibSharp resolver), PluginConfiguration (lanes etc.),
                         config web page (tabs: Dashboard, Review, History, Try it, Settings), API (/emby/LabelMaker/...), background worker,
                         library refresh, admin push, preflight checks
  tests/LabelMaker.Tests/ unit tests, fixtures (generated tiny audio files; recorded MusicBrainz JSON), golden-set regression
  docs/                  DESIGN, PHASE0, HANDOFF, releases/, reference/, spikes/
```
Copy from Hall Pass: project layout, `Plugin.cs` shape (BasePlugin<PluginConfiguration>, IHasWebPages, IServerEntryPoint), `[Authenticated(Roles="Admin")]` services, embedded HTML/JS page, evidence/version-token idea, CI and release workflow (action pins), docs set (README, SECURITY, TESTING, CLAUDE.md, HANDOFF). csproj needs `MediaBrowser.Server.Core 4.9.1.90`, `net6.0`, `RestorePackagesWithLockFile`, and the embedded TagLibSharp resource (`$(NuGetPackageRoot)taglibsharp/2.3.0/lib/netstandard2.0/TagLibSharp.dll` with `LogicalName="TagLibSharp.dll"`).

## 6. Build plan (DESIGN.md section 13, adjusted)
Phase 1: Core (tags, MusicBrainz client with fixtures, matcher incl. `.albuminfo` path and partial albums, naming script, sanitiser, planner, mover). Calibration set built from the owner's correct library (strip/scramble tags, include partial albums); auto-apply precision target 99%+.
Phase 2: pipeline and state machine, dry run, undo, crash tests.
Phase 3: plugin shell, settings page with lane editor and preflight, API, library refresh.
Phase 4: plugin review UI.
Phase 5: cover art (Cover Art Archive) and websocket push; AcoustID later.
Phase 6: Embidextrous Library > Label Maker tab, Home card, notification rules (section 7 below), emulator check.
Phase 7: trial in dry run on the owner's real server (first check that the account Emby runs as can write to the shares), then auto-apply on one lane.
Phase 8: docs, release only on "publish it".

## 7. Embidextrous work requested (do the notifications first)
Full text: `DESIGN.md` section 19 and `C:\dev\embidextrous\docs\notifications-plan.md`. Summary: **configurable notification rules per service/plugin in Embidextrous Settings**, any number of rules, e.g. Hall Monitor "when a user has used X% of their time" (several X values), "when a user has used all of their time" (existing alert, keeps the +15/+30/+60 buttons); Label Maker "N albums need review", failures, lane problems. Rules live on the phone; pure-Kotlin unit-tested rule engine; per-service Android notification channels; the current Hall Monitor alert switch migrates to the first default rule. Existing code to build on: `watching/AlertService.kt` (foreground service holding the live websocket), `watching/LimitAlerts.kt` (current fire-once logic, tested), `data/HallMonitorModels.kt` `UserStatus` (has `secondsWatchedDaily/Weekly/Monthly`, `effectiveDailyLimitMinutes`, `secondsRemaining`, `limitingPeriod`, `limitingAllowedSeconds`, `blockReason`: percent used can be computed as `1 - secondsRemaining / limitingAllowedSeconds` for the limiting period; verify against live status before relying on it), `ui/SettingsScreen.kt` (Background alerts switch today), `data/AppSettings.kt` (settings store). Embidextrous decisions and history to read: its `docs/HANDOFF.md` (Hall Pass tab pattern, version gates, emulator recipe), `docs/decisions.md` (no app lock, notification protections, alerts clear on every phone, push over the websocket is fire-and-forget), `docs/plan.md`.

## 8. How this owner wants work done (copied from the earlier sessions' memory; a new session will not have it)
- Plain-language answers, lead with the result; American English; full sentences in UI copy; no sentence ending in "is"/"are"; say where a setting lives. Review all copy on a page when one line is criticised.
- **Verify, do not guess**: reproduce problems with real files and show evidence; measure layouts (script over every control) and look at screenshots of every page and section; report what was NOT verified.
- Ask before behaviour changes; give options with a recommendation when the owner says they have no ideas; ask only for decisions that are truly theirs.
- **Release flow**: only when the owner says "publish it": notes `docs/releases/vX.Y.Z.md` committed first; CI and the CodeQL jobs green on that exact commit; `git tag -a vX.Y.Z` (tag = AssemblyVersion); push; the release workflow builds; report size and SHA-256. Check GitHub through PowerShell `Invoke-RestMethod https://api.github.com/repos/feerlessleadr/<repo>/...` (no `gh`; Bash curl has no network; 60 requests/hour, poll slowly).
- Push to GitHub: the owner has allowed pushing to their repos for ordinary commits. Private details (LAN IP, share names, username, Picard.ini) are kept out of tracked files; see `docs/local/ENVIRONMENT.md`. Check with a grep before every push.
- Secrets: never display or store keys/passwords; the test server's admin key is read from `%USERPROFILE%\.emby-spike-key` by scripts and never printed; throw-away users and keys only, delete afterwards.
- When the owner is away they say so ("no UAC prompts"): do only non-elevated work.
- Never leave the test server changed: after tests restore settings, delete throw-away users/files, remove spike plugins.
- Tool traps: Bash here-docs with apostrophes or perl braces can fail to parse, so write scripts/files with the Write tool; PowerShell 5.1 (no `&&`, no `-AsHashtable`); a path guard blocks `Remove-Item` combined with certain path text, use `-LiteralPath`; plugin pages are cached by plugin version (bump the version when a page changes; use a fresh browser tab); in the Android emulator type the password after `Tab`; Gradle needs `JAVA_HOME` = Android Studio's JBR; no Python/Node installed.

## 9. Immediate next steps
1. (Done 2026-10-13) Private details stripped from the tracked docs; Picard.ini received and kept local. Next: the matcher (see section 11).
2. Scaffold the repo from Hall Pass (solution, projects, CI, README/SECURITY/TESTING/CLAUDE.md, LICENSE MIT, third-party notice for TagLibSharp LGPL-2.1).
3. Phase 1: write the Core pieces with tests; start with the naming script engine, sanitiser, `.albuminfo` reader and MusicBrainz client (fixtures recorded from the owner's samples: Green Day *Insomniac* release `46b2741f-10f2-4c3e-9f3d-2c55abfa6f51`, Leonard Cohen *Various Positions*).

## 10. Not verified yet
Everything on the owner's real Windows server (share permissions for the account Emby runs as, UNC paths, speed of copy over SMB), MusicGrabber's reaction to files leaving its folder, large drops (hundreds of albums), non-Latin names, classical music, the optional local-path shortcut for library folders on the same machine, Cover Art Archive behaviour, ID3 version/cover settings of the owner's Picard.

## 11. Progress (2026-10-13, second session)
- **Picard.ini received** (`docs/local/Picard.ini`, copied in by the owner; it was not at `%APPDATA%\MusicBrainz\Picard.ini` on this PC). Facts that change the defaults:
  - `write_id3v23=false`: Picard writes **ID3v2.4**; `id3v2_encoding=utf-8`. The owner chose "match Picard", so the engine writes ID3v2.4 UTF-8 (DESIGN.md said 2.3; changed).
  - `write_id3v1=true`: Picard keeps the ID3v1 footer. The owner chose to match, so it is kept (PHASE0 said always remove it; changed).
  - `remove_ape_from_mp3=false`: APE tags are left alone.
  - `move_additional_files=true` with `move_additional_files_pattern=*.jpg *.png`: Picard moves images only, **not `.lrc`**. DESIGN.md says `.lrc` moves with its track; still to decide.
  - `save_images_to_tags=true`, `cover_image_filename=cover`, `local_cover_regex` matches cover/folder/albumart.
  - Naming: selected script "Preset 1" (the owner's stock default, same text as `docs/reference/picard-default-naming-script.txt`).
  - Picard's own move target is one of the shares (see the local-only environment file); Label Maker lanes come from `DESIGN.md` 16.3 instead.
- **Engine built** in `src/LabelMaker.Core` (net6.0) with tests in `tests/LabelMaker.Tests` (net8.0): `dotnet test tests/LabelMaker.Tests` gives 56 passing.
  - `Naming/PicardScript.cs`: the script subset (%vars%, $if, $if2, $and, $or, $gt, $num), validated with clear messages; values have "/" removed.
  - `Naming/PathSanitiser.cs`: Windows-safe components, reserved names, 120-character cap, NFC.
  - `Naming/NamingVariables.cs`: Picard's variable names; `_multiartist` is approximated (track artist differs from album artist). **Check against a real Picard compilation.**
  - `Inbox/AlbumInfo.cs`: `.albuminfo` reader (64 KB cap, invalid release ids dropped).
  - `Tags/TrackTagFile.cs`: TagLibSharp read and write. ID3v2.4 by default, ID3v1 per option, custom MusicGrabber frames preserved (tested for MP3 and FLAC).
  - `MusicBrainz/MusicBrainzClient.cs`: release lookup by id, one request per second, Retry-After and back-off on 503/429, refuses a User-Agent without a contact. Tested with a fake handler and a recorded release (`Fixtures/mb-release-insomniac-2021.json`).
- **Not built yet (next):** the matcher (release-group search, track verification, `.albuminfo` as a direct hint, partial albums), the scorer and assignment solver, the planner, the applier (backup, verify, cross-volume move), the persistent MusicBrainz cache, cover art, the plugin shell, the review UI. The golden-set precision test (DESIGN.md 5.5) needs the owner's library.
- **Not verified:** anything on the owner's Windows server (shares, the account Emby runs as); the Picard compilation rule above; `.lrc` movement; whether MusicGrabber re-downloads a moved track.
- **Private details** (LAN IP, share names, username) were stripped from the tracked files, and the git history was rewritten on 2026-10-14 (all earlier commits squashed into one clean root commit, force-pushed) so none of it is in any commit. GitHub may still serve the old commit by its SHA for a while; only GitHub support or recreating the repo removes that.

## 12. Progress (2026-10-14): matcher and pipeline core
- **Matcher** (`src/LabelMaker.Core/Matching`): `AlbumMatcher` tries MusicGrabber's `.albuminfo` release id first (one lookup, verified), else a release-group search, then checks every official edition of up to 3 groups against the files. `ReleaseVerifier` scores file to track (title 0.6, track number 0.2, disc 0.1, duration 0.1; each only when both sides have it) and pairs files with tracks using the Hungarian algorithm (`AssignmentSolver`), so a title repeated on two discs goes to the right disc. Outcomes: Auto, NeedsReview (a weak file, only probable, rival editions), NoMatch. Editions with the same recordings in the same places are interchangeable (preferred country, then earliest date). Thresholds (0.92 auto, 0.60 review, 0.5 per file, 0.05 margin) are DESIGN.md's starting values and are not calibrated: that needs the golden set from the owner's library.
- **Real-data checks:** Insomniac (2 discs, 8 repeated titles) and Various Positions (13 official editions, same recording in all: picked US 1984 automatically).
- **Pipeline** (`src/LabelMaker.Core/Pipeline`): `Planner` (new tags from the release, Picard path, existing-folder reuse, path length and containment checks, duplicate detection, `.lrc` sidecars and one `cover.<ext>`), `Applier` (two phases: tag and verify every file in the inbox, then move; journal saved after every step; `Resume` and `Undo`), `SafeMover` (rename on one volume; otherwise copy, verify size and SHA-256, rename, then delete the source), `JournalStore` (atomic JSON per album), `IFileOps` (so tests inject failures). Tested on real temp folders, including a crash at each move, copy and delete, and a read-only file.
- **Choices I made (change if you disagree):** `.lrc` moves with its track (owner agreed); the first of cover/folder/albumart is moved as `cover.<ext>` and an existing library cover is never replaced; after a successful move the inbox album folder's leftovers (`.albuminfo`, second cover, Thumbs.db) are deleted so the folder can go, but their contents are saved in the journal and Undo restores them; undo restores only the tag fields Label Maker owns (the ID3 version and encoding stay as written); a duplicate in the library is refused, never overwritten.
- **Not built yet:** the inbox scanner (stable-for-N-seconds detection, grouping folders into albums, state machine, queue), the persistent MusicBrainz cache, cover art from the Cover Art Archive, track-level search fallback for files with no album tag, folder-name parsing, the plugin shell, the review UI, the golden-set calibration.

## 13. Calibration results (2026-10-14, first real run)
- **Data:** 322 albums from the owner's library (three exports, kept in git-ignored `docs/local/`); 287 carry one consistent MusicBrainz release id (the answer key). Of those, 271 are trustworthy (the files fit their own release at 0.90 or more); 16 are not (edited tags, a release changed on MusicBrainz, one deleted id). Real library albums average about 3 tracks, so partial albums are the normal case. `tools/LabelMaker.Calibrate` reruns this from the on-disk MusicBrainz cache at no cost (`dotnet run -- --cache <dir> --out <report.md> --editions titles|recordings <exports>`).
- **Result at the starting threshold (0.92), trustworthy albums, tags as they are:** strict edition rule (same recordings): 72% applied automatically, 100% precision (every threshold from 0.90 up is 100%; 99% at 0.60 to 0.80). Title rule (same titles in the same places are interchangeable): 83% applied, 100% right album and track titles; 97% by exact recordings, because six albums got another edition of the same album that lists different recordings (for example a remaster). All albums, including the untrustworthy ones: 97.5% strict at 0.92 (the extra misses are the answer key disagreeing with the tags, not the matcher).
- **What the misses were:** the library's own release was never a candidate in two cases (a different release group; both scored 0.805, under the threshold, so they would go to review). The rest were key problems.
- **"[FLAC]" noise in the album name:** after `AlbumNameCleaner`, 100% precision at 0.92 on trustworthy albums, 70% applied.
- **Not matched at all (14 of 287):** soundtracks and Various Artists albums, names with a Unicode hyphen (U+2010) or "(sfw)" suffixes, a few others. Next fixes: normalise dashes in the search, loosen the search when the quoted one finds nothing, a recording-search fallback.
- **Decision for the owner:** edition rule. Recommendation: `SameTitles` by default (72% to 83% automatic, no wrong album); the cost is that the recording ids written may come from another edition of the same album.
- **Not measured yet:** the 32 albums with no id (mostly the kids library), the real MusicGrabber path (`.albuminfo`), and albums with mixed ids.

## 14. Plan: "not matched at all" and "not measured yet" (2026-10-14)
Rules are explained in plain English in `docs/MATCHING-RULES.md` (the settings page should reuse that wording). The edition rule now defaults to **same titles** (owner decision).

**Found by calibration, fixed:** ID3v2.3 files store several artists separated by "/", so TagLib read the band "AC/DC" as "AC" and "DC" and the reader (and the exporter script) took "AC". The reader now puts the name back together for ID3v2.3 files (ID3v2.4, which Label Maker writes, was never affected). Two tests cover it.

### Build plan, in order
1. **Diagnose each of the 14 unmatched albums** (add to the harness: the query sent, how many groups came back, the top group, why it was filtered). Several causes are only guessed so far.
2. **Cheap search fixes:** treat all dash and quote variants as plain ones (a Unicode hyphen broke "blink-182"); strip "(sfw)", "(nsfw)", "(explicit)", "(clean)"; if the exact quoted search finds nothing, retry without the quotes, then without the artist, then as a release search; for an artist name with "/" in it, also try the part before the slash; for soundtracks and "Various Artists", search without the artist.
3. **Recording fallback:** when the album search still finds nothing, search recordings by title and artist for up to three files, collect the releases that contain them, and check them with the same scoring and the same rules. A result found this way is never applied automatically unless every checked file points to the same release and the score is 0.92 or more; otherwise it goes to review.
4. **Use ids already in the tags as a hint:** a Picard-tagged file carries its release id. Look it up and verify it like MusicGrabber's `.albuminfo`; if the files in one folder carry different release ids, send the folder to review with the reason "These files carry different release ids". (This also covers the 3 mixed-id albums in the test.)
5. **Re-run the calibration** and compare. Targets: unmatched under 2% of the answer-key albums, precision unchanged at 100% for trustworthy albums.

### Plan for what was not measured
- **32 albums with no id (mostly the kids library):** after steps 1 to 4, run the matcher on them and give the owner a short yes/no list ("Label Maker says this album is X: right or wrong?"). Those answers become an answer key for the kids lane, which is the lane most likely to hold untagged music.
- **MusicGrabber's `.albuminfo` path:** add two scenarios to the harness, "hint with the right release id" (expect automatic where the files fit, one lookup) and "hint with a wrong id" (another edition, another album: expect it to be ignored and the search to run, never a wrong automatic apply). Then measure the real thing in the dry-run trial on the real inbox (how many folders have the file, how often it checks out).
- **Albums with mixed ids:** step 4 above; in production MusicGrabber does not write ids, so these should be rare.

### What happens in production when an album is not matched
- **No match:** the album stays in the inbox untouched (owner decision) and is listed under "Needs review, No match" with the plain reason; the phone gets the count. The owner can search by hand, paste a MusicBrainz link, choose "keep my tags and file it", skip, or send it to the trash folder. "Keep my tags" is refused unless artist, album and title are all present, so it cannot create an "Unknown Artist" folder.
- **MusicBrainz down or busy:** the album waits and is retried; it is never counted as "no match".
- **A better matcher later:** items still in review are re-checked when the matching version improves; finished albums are never touched.
- **Situations not yet measured:** they default to review, not automatic; automatic applying is switched on one lane at a time after a dry-run trial on that lane's real data (MATCHING-RULES.md section 7b).

## 15. Search cascade and the unmatched albums (2026-10-14, later the same day)
Build plan steps 1, 2 and 5 of section 14 are done; step 4 (use release ids already in the tags as a hint) and step 3 (recording fallback) are not, because only 1 of 287 albums is still unmatched.
- **Diagnosis (step 1).** `MatchResult.Notes` now lists every search (shape, album and artist asked for, what came back, what was kept) and the best edition checked; the calibration report prints it for each unmatched album. Real causes found: the exact "album AND artist" query often returns nothing or the wrong albums (a live probe showed `releasegroup:"X"` alone finds the album when the combined query finds none); a Unicode hyphen in "blink-182"; "(sfw)" suffixes; a trailing "(Green)" or "(Soundtrack from ...)" in the tags; "Frozen 2" versus "Frozen II"; a letter-spaced title ("G I R L"); an artist credited under another name ("mgk" is "Machine Gun Kelly"); a renamed album ("Greatest Hits - Chapter One" is "The Hits - Chapter One"); a subtitle ("The Little Mermaid: Original Motion Picture Soundtrack" is "The Little Mermaid"); and, for "AC/DC - High Voltage", only 3 of the 4 same-named albums were browsed.
- **The cascade (step 2).** `SearchShape` (`QuotedAlbumAndArtist`, `QuotedAlbumOnly`, `PlainWords`, `ArtistAlbums`) in `MusicBrainzClient`; the matcher tries each shape for each album-name variant (`AlbumMatcher.AlbumNameVariants`: cleaned, without any trailing "(...)", numerals swapped) until some edition scores at least the review threshold, with at most 8 searches. `CleanForQuery` turns every dash and quote into a plain one. `TitleNormaliser` now treats Roman numerals II to IX as digits, `AlbumSimilarity` accepts "name plus subtitle" (0.9), and "G I R L" equals "Girl" (0.95). `ArtistsAgree` (similar names, a shared name part, or Various Artists) filters albums found by an album-only search; if none pass, the matcher also tries an album with the right name under another artist name and an album by the same artist with a similar name, and **an album found that way can never be applied automatically** (reason: "MusicBrainz lists this album under a different name or artist than your tags say, so please check it."). `MaxReleaseGroups` is now 5.
- **Result (default title rule, bar 0.92):** unmatched 14 to 1 (0.3%); 85% of the 271 trustworthy albums applied automatically and 232 of 232 on the right album with the right titles in the right places; with "[FLAC]" noise 82% and 219 of 219; strict rule 73% and 199 of 199 exact. The one unmatched album is a brand-new soundtrack crowded out by same-named albums. `docs/MATCHING-RULES.md` section 8 has the plain-English summary.
- **Other changes:** `InternalsVisibleTo("LabelMaker.Tests")`; the test suite runs one test at a time (TagLib's ID3 settings are process-wide); 157 offline tests; the harness takes `--editions titles|recordings` and writes a "trustworthy albums" table.
- **Lesson:** a PowerShell script that edits files with .NET calls uses the process folder, not PowerShell's location; set `[Environment]::CurrentDirectory` or start the script from the repo root.
- **Next (unchanged order):** the inbox scanner and queue (Phase 2), then the plugin shell. Still to do from section 14: step 4 (ids in the tags as a hint), the MusicGrabber hint scenarios in the harness, a yes/no list for the 32 albums with no id, the persistent MusicBrainz cache inside Label Maker, Cover Art Archive, the Singles layout.
