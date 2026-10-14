# Label Maker: design and build plan (music ingest plugin + Embidextrous integration)

Status: design written 2026-10-13 and kept as decided; the engine is built since (see `docs/HANDOFF.md` section 0 for the current state, `docs/MATCHING-RULES.md` for the rules in plain English, `docs/NOTIFICATIONS.md` for the push contract added 2026-10-14). Where a later decision changed this text, the later document wins and says so. The name **Label Maker** was chosen by the owner on 2026-10-13. Still to fix before any code (renames are costly, see the Hall Monitor rename): check the GitHub repo name, plugin name and DLL name are free; DLL `LabelMaker.dll`, routes `/emby/LabelMaker/...`, settings file `LabelMaker.xml`, repo `label-maker-emby` (owner creates the repo).

## 1. What it does, in one paragraph
Watches one or more "inbox" folders. When a download has finished arriving, it groups the files into albums, finds the matching release on MusicBrainz (using the existing tags, the file and folder names and, optionally, an audio fingerprint through AcoustID), and scores the match. If the score is high enough and a set of safety checks pass, it writes clean tags and cover art, renames the files, and moves the album into the library folder that belongs to the inbox it arrived in (`/downloads/music` -> `/music`, `/downloads/kidsmusic` -> `/kidsmusic`). If not, it leaves the album where it is, puts it on a review list, and Embidextrous tells you. You review on the phone: pick another candidate, search by hand, paste a MusicBrainz link, keep as is, or skip.

## 2. Decisions to make first (with my recommendation)
| # | Question | Recommendation | Why |
|---|---|---|---|
| 1 | Plugin or standalone app? | **Emby plugin whose engine is a separate Emby-free class library** (same split as Hall Pass: `Core` + plugin shell). | One install path (GitHub Repo Plugin Install), Emby admin login already exists, the plugin can read Emby's library folders and trigger a scan, and Embidextrous already knows how to talk to plugins. Keeping the engine free of Emby types means it can later be wrapped as a Docker/CLI tool if Emby's plugin API ever hurts. |
| 2 | Why not just script Picard or use beets/Lidarr? | Build our own. | Picard has no headless mode. beets (`beet import -q`) does auto-match well, but has no phone review, no per-folder lane routing UI and needs Python in the container. Lidarr matches but is about acquiring. Worth reading beets' matching code for ideas; not a dependency. |
| 3 | Fingerprinting (AcoustID) | **Optional, off by default, needs `fpcalc` (Chromaprint) path**; text matching is the default. | It is what rescues files with no or wrong tags, but it needs a native binary (Linux/Docker servers often have none) and an AcoustID application key. Spike first (section 12). |
| 4 | In-place tagging or copy? | Tag **in place in the inbox after saving the original tags**, then move. | The inbox files are your downloads (already copies). A crash leaves a tagged file in the inbox, which is safe and re-runnable. |
| 5 | Notifications | **Push over the Emby websocket to Embidextrous's existing `AlertService`, polling only as a backup. No outside service.** (Owner decision.) | Same mechanism as Hall Monitor's limit alerts. See section 9. |
| 6 | How do "lanes" work? | A **lane** = inbox folder -> library folder, plus per-lane settings. | Matches what you described, and each lane can have its own thresholds, naming template and owner (kids' lane can be stricter or always require review). |
| 7 | Default mode | **Dry run on**, like Hall Pass: it matches and proposes but changes no files until you switch it off. | Same safety net the owner has already approved; the first week is for calibrating thresholds. |

## 3. Pipeline
```
inbox file appears
  -> STABLE?        (size/mtime unchanged for N seconds, not a temp name, not open for write)
  -> GROUP          (folder = album candidate; split a folder if tags show several albums;
                     merge CD1/CD2 subfolders)
  -> READ           (tags via TagLibSharp, duration, bitrate, existing art, sidecars)
  -> CANDIDATES     (MusicBrainz search by album+artist, track count, barcode/catalog no. if present,
                     MBIDs already in the tags -> direct lookup; optional AcoustID per track)
  -> SCORE          (release-level + per-track assignment, section 5)
  -> DECIDE         (auto / review / no match, section 5.4)
  -> PLAN           (new tags, new file names, destination, what is replaced, what is left behind)
  -> APPLY          (back up old tags -> write tags + art -> verify by re-reading -> move -> clean inbox)
  -> REFRESH        (ask Emby to scan the destination folder; Hall Pass auto-scan will see the new tracks)
```
State per album: `Waiting` -> `Matching` -> (`NeedsReview` | `Ready` | `NoMatch`) -> `Applying` -> `Done` | `Failed` | `Skipped` | `Duplicate`. State is persisted (JSON file or SQLite in the plugin data folder) so a restart mid-way resumes.

## 4. Lanes and configuration
Settings are grouped like Hall Pass (sections, plain sentences, no sentence ending in "is"):

**Lanes** (list; each row):
- Name ("Mine", "Kids", "Shared").
- Inbox folder (absolute path as the Emby server sees it).
- Library folder (absolute path; must be inside an Emby music library, the page warns if not).
- Keep sub-folders? (`/downloads/kidsmusic/Bluey/...` -> keep as is, or let the naming template decide). Default: template decides.
- Auto-apply allowed (on/off). Off means "always ask me" for this lane.
- Threshold override (otherwise global).
- Naming template override (otherwise global).
- Library refresh after move (on/off).
- Notify who (admin users; a lane for the kids can notify only you).

**Matching**: auto-apply threshold (default 0.92), review threshold (default 0.60), preferred release countries (e.g. US, XW, GB), preferred formats (Digital Media first, then CD), prefer original release vs reissue (default: earliest release of the group in the preferred country), MusicBrainz base URL (default `https://musicbrainz.org/ws/2`, a self-hosted mirror removes the rate limit), contact string for the User-Agent (required by MusicBrainz), AcoustID on/off + application key + `fpcalc` path.

**Writing**: ID3 version (2.3 default for car/old-player compatibility, 2.4 optional), strip ID3v1/APEv1 leftovers (default on), embed cover art (size 500/1200/original from the Cover Art Archive, default 1000 px front), also save `cover.jpg` (default on), keep existing art if the album has none on MusicBrainz, which extra tags to write (MBIDs, original date, label, catalog number, barcode, ISRC, release type, artist sort, genres from MusicBrainz tags: default off because crowd tags are noisy), which tags to preserve (lyrics, ratings/play counts, replay gain, comments: default preserve everything we do not own).

**Naming template** (default mirrors a typical Picard script):
**The owner's real Picard script** (`C:\claude\picard-default-naming-script.txt`, Picard's stock script) is the default, reproduced exactly:
```
$if2(%albumartist%,%artist%)/
$if(%albumartist%,%album%/,)
$if($gt(%totaldiscs%,1),$if($gt(%totaldiscs%,9),$num(%discnumber%,2),%discnumber%)-,)$if($and(%albumartist%,%tracknumber%),$num(%tracknumber%,2) ,)$if(%_multiartist%,%artist% - ,)%title%
```
So: `Artist/Album/01 Title.ext`; discs go in the file name (`1-01 Title`, two-digit disc number above 9 discs), never sub-folders, which matches the library's "perfectly organized into artist\album folders, tracks directly in the album folders" setting; no year in the folder name; compilations get `Various Artists/Album/01 Track Artist - Title`. Tracks with no album artist land directly in the artist folder (a quirk of the stock script, kept for parity, flagged in review). The template engine therefore needs a small Picard-script subset (`$if`, `$if2`, `$and`, `$gt`, `$num`, `%var%`, `_multiartist`); users can edit the template in settings. Sanitising rules in section 6.

**Behaviour**: stable-time seconds (default 60), poll interval (default 60 s; watchers are not trusted, section 7), max albums per run, what to do on **no match** (default: leave in inbox and list under "Needs review > No match"; alternatives: move to `<library>/_Unmatched` or move with cleaned existing tags only), what to do on **duplicate** (default: leave and ask), keep the backup of old tags for N days (default 30), delete empty inbox folders after a successful move (default on), dry run (default on), pause switch.

## 5. Matching in detail (the hard part)
### 5.1 Gathering the query
Per album: most common `albumartist` else `artist`, most common `album`, track count, total duration, disc count, year, barcode/catalog number if tagged, any existing `MUSICBRAINZ_ALBUMID` / `MUSICBRAINZ_RELEASEGROUPID` / track MBIDs (a direct hit is trusted but still verified against track count). If tags are missing or junk ("Track 01", "Unknown Artist"), fall back to parsing the folder name (`Artist - Album (Year) [FLAC]`) and file names (`01 - Artist - Title`), strip noise such as `[FLAC]`, `(320kbps)`, `www.site.com`, `Deluxe Edition` for the search (keeping the edition for the scoring), and, if enabled, fingerprint tracks.

### 5.2 Candidate search
- MusicBrainz `release` search (Lucene) with several query shapes, tried in order until enough candidates: exact fields (`release:"..." AND artist:"..."`), loosened (no quotes, stripped edition words), by `tracks:` count, and by barcode/catno when present. Up to ~25 candidates.
- Lookup each candidate with `inc=recordings+artist-credits+release-groups+labels+media` (needed for track lists). **Rate limit 1 request/second, one worker, retry with back-off on 503**, cache every response on disk (releases rarely change) so re-review and re-matching cost nothing.
- AcoustID: fingerprint each track (long files are slow, so cap the analysed seconds), look up recordings, collect the releases those recordings appear on, and count how many tracks vote for each release. Strong signal for albums with bad tags.

### 5.3 Scoring (modelled on Picard's, simplified)
Release score = weighted sum, each part 0..1:
- Album title similarity (normalised: case, punctuation, "&"/"and", accents, "the", edition words discounted) .25
- Album artist similarity (credit join phrases like "feat." handled; Various Artists special case) .20
- Track count equal (1.0), differs by one (0.4), else 0 .15
- Per-track assignment quality .30: build a matrix of (file, MB track) scores from title similarity, duration difference (tolerance +-3 s full, falling to 0 at +-15 s), track number agreement and fingerprint vote, then solve the best one-to-one assignment (Hungarian algorithm); the average of the assigned scores, penalising unassigned files/tracks.
- Date/country/format preferences .10 (tie-breaker between editions, never enough to make a bad match good).

### 5.4 Auto-apply rule (all must hold, otherwise the album goes to review)
1. Best candidate score >= lane threshold (default 0.92).
2. Every file is assigned to a distinct track of the release; missing tracks are fine (partial albums are normal, see section 18).
3. **Margin**: second-best candidate is at least 0.05 lower, or both candidates are the *same release group* (just different editions) and the preference rules pick one. This is the guard against "which of these 9 reissues".
4. No track's own assignment score under 0.5 (one wrong file should not hide in a good average).
5. Lane allows auto-apply; dry run is off.
6. The destination does not already hold this album (duplicate policy).
Any failure records a plain-language **reason** (like Hall Pass reasons) so the phone shows why it needs review: "Three editions match equally", "2 of 12 tracks could not be matched", "Album already in your library", "No release found", "Only the artist matched".

### 5.5 Calibrating thresholds from facts, not guesses
Use the owner's existing, already-correct library as a test set: copy ~200 albums, strip or scramble their tags in realistic ways (missing album, "Track 01" titles, wrong artist case, extra "[FLAC]" noise), run the matcher, and measure how often the best candidate equals the known-correct MBID at each threshold. **Target: auto-apply precision of 99%+ (a wrong auto-tag is worse than a review).** This golden set becomes the regression suite (recorded MusicBrainz responses as fixtures so tests are offline and deterministic).

## 6. File and tag handling problems and solutions
| Problem | Solution |
|---|---|
| Download still in progress (phone sync, Syncthing, browser temp files) | Stable-for-N-seconds rule on size + mtime of every file in the group; ignore `.part`, `.tmp`, `.crdownload`, `.!sync`, `.syncthing.*`, `~` names; also try opening for exclusive read. A folder only becomes eligible when nothing in it changed. |
| File watchers unreliable (Docker bind mounts, SMB/NFS, Windows UNC) | Use `FileSystemWatcher` only as a nudge; the real trigger is a **poll** plus a scheduled task and a "Scan inbox now" button. |
| Same file seen twice / app restarted mid-move | Idempotent state machine keyed by album fingerprint (sorted file names + sizes + mtimes); every step checks before acting. |
| Cross-filesystem move (`/downloads` and `/music` on different volumes) | Move = copy to `<dest>.labelmaker-tmp`, verify size and hash, rename into place, then delete the source. Same-volume move = atomic rename. Never delete the source before the destination is verified. |
| Overwriting existing library files | Never overwrite silently. Destination exists -> `Duplicate` state with options: keep both (suffix), replace if the new one is better quality (lossless over lossy, higher bitrate; shown, never automatic by default), or discard the new one. |
| Illegal characters / long paths | Replace `\ / : * ? " < > |` and control characters, trim trailing dots/spaces, cap each component at 120 chars and the whole path under 240 (Windows limit) / 255 bytes per name (Linux), handle reserved Windows names (CON, NUL), normalise Unicode to NFC, avoid names that collide only by case. Same rules on every OS so a library is portable. |
| Special characters in artist names (AC/DC, "?", "P!nk") | MusicBrainz name kept in the tags exactly; only the *file name* is sanitised (`AC_DC`, `P!nk`). Configurable replacement character. |
| Multi-disc releases | Disc/track numbers and totals written; files named `1-01`, `2-01`; optional `Disc 1` sub-folders. Input in `CD1/CD2` sub-folders is merged into one album before matching. |
| Compilations / Various Artists | `albumartist = Various Artists`, compilation flag set (`TCMP`/`cpil`/`COMPILATION`), per-track artist kept. |
| Singles and loose files in one folder | Group by album tag first; files without an album go through single-track matching (fingerprint or artist+title search) and are filed under their release; unmatched loose files stay in review. |
| Several albums in one folder | Split by (albumartist, album) when the tags clearly disagree and the groups are each plausible albums. |
| No usable tags | Filename/folder parsing, then fingerprint (if enabled); otherwise "No match", never guess. |
| Different formats | Per-format writers: MP3 (ID3v2.3/2.4, UTF-16/UTF-8 text, APIC art), FLAC (Vorbis comments + PICTURE), M4A/MP4 (atoms, `covr`), Ogg/Opus (Vorbis comments, METADATA_BLOCK_PICTURE), WAV/AIFF (ID3 chunk, flag as limited). Tag names follow Picard's so Picard later reads the files as already matched. |
| Losing existing data | Everything we do not own (lyrics, play counts/ratings, replay gain, comments) is preserved; the old tag set is saved as JSON before writing. |
| Sidecar files | `.lrc`/`.txt` lyric files that belong to a track (Hall Pass reads them) are **renamed and moved with their track**; `cover.jpg`, `.cue`, `.log`, booklet PDFs move with the album into its folder (configurable); junk (`Thumbs.db`, `.DS_Store`, `.url`) is dropped. |
| Cover art | Cover Art Archive front image at the chosen size (through the plugin, cached); if the release has none, keep the embedded art already in the files; if images differ, mention it in the review card. Size limit on embedded art so files do not balloon. |
| Wrong match auto-applied | **Undo**: journal per album (old tags, old path, new path); Undo restores the tags and moves the files back to the inbox (like Hall Pass undo). Backup kept N days. |
| Emby scanning half-moved files | Inbox is **not** in an Emby library (the page warns and offers to refuse if it is). Files arrive in the library by atomic rename; the plugin then asks Emby to scan only that folder. |
| Emby's own metadata after the move | MusicBrainz IDs in the tags let Emby's music providers match artists/albums correctly; no extra work. A scan request after the move picks it up, and Hall Pass's new-track auto-scan evaluates explicit content afterwards. |
| Permissions (Docker user, SMB) | Run a **preflight** on every lane: can read/write/delete in the inbox, can create/rename in the library, same-volume or not, free space (>= album size x 1.1). Shown as green/red checks on the settings page with the exact fix ("the Emby user cannot write to /music"). Path mapping note: paths are those *inside* the Emby container. |
| Non-Latin names / transliteration | Setting: keep original script (default) or use MusicBrainz's English/transliterated alias for file names; tags keep the artist credit as released. |
| Classical, audiobooks, podcasts | Out of scope for v1: detect (genre/very long tracks/`Classical`) and send to review as "needs manual care". Audiobooks/podcasts not touched. |
| Disk space | Check before copy; refuse with a clear failure rather than a half album. |
| Archives (`.zip`, `.rar`) | v1 ignores them and says so in the review list ("archive found, extract it"); extraction is a later option (zip-slip protection needed). |

## 7. Operational problems
- **MusicBrainz rules**: a meaningful `User-Agent` with a contact (set in settings; refuse to run without), max 1 req/s, honour `503`/`Retry-After`, cache aggressively, support a mirror URL. Cover Art Archive and AcoustID have their own limits (AcoustID: 3 req/s, free key per application).
- **Offline / MusicBrainz down**: albums stay `Waiting` and retry with back-off; nothing is failed because of a network blip, and never auto-applied from a partial lookup.
- **Large drops** (200 albums at once): single worker, fair queue (oldest first), per-album progress; the 1 req/s limit means roughly 15-40 s per album; the page and app show "N waiting".
- **Resource use inside the Emby process**: one background worker, `Task.Run` with cancellation on shutdown, fingerprinting spawned as a child process with a timeout, no big files in memory (stream hashing).
- **Concurrency with the user**: while an album is in review, its files are not touched; "Applying" locks the album; the review UI refuses stale actions (version number per album, like Hall Pass's evidence version).
- **Logging**: plain log lines per album with the reason; never log API keys or tokens; the AcoustID key is write-only in the API (like the GitHub token elsewhere).
- **Updates**: a "matching version" token (like Hall Pass `stages-vN`) so a better matcher can re-evaluate items still in review (never re-touches completed ones).

## 8. Security and privacy
- All API routes `[Authenticated(Roles="Admin")]`; the app uses the signed-in administrator's token (same as the Hall Pass tab).
- **Path safety**: lane paths must be absolute and exist; destination = library folder + sanitised relative path, checked after normalising that it stays inside the library folder (no `..`, no symlink escapes: resolve and compare); the API never takes a path from the client except an album id.
- MusicBrainz/AcoustID data is untrusted text: strip control characters, cap lengths, never use it unsanitised in paths, never render as HTML (the plugin page uses text nodes).
- Privacy: the only data leaving the server is tag text (album/artist/title/track count), optionally fingerprints (AcoustID) and the MBIDs for cover art. State this in the README and next to each switch. Fingerprinting off by default. 
- No deletion by default: "discard" actions move to a `_Trash` folder inside the inbox; permanent deletion only after N days and only via an explicit setting (Prohibited-style actions stay with the owner).

## 9. Embidextrous integration
Follows the Hall Pass tab pattern exactly (`data/HallPass.kt`, `ApiClient.hp*`, view model, screen, plugin version gate, administrator token, "too old" card):

**Library > Label Maker tab** (admin profile):
1. **Summary card**: inbox status per lane (waiting, processing, needs review, done today), last run, pause/resume, dry-run state, "Check inbox now", preflight problems (red) if any.
2. **Needs review list**: album cards with cover (the plugin proxies and caches Cover Art Archive thumbnails so the phone contacts only the server), "Your files" vs "Best match", the **reason** in a sentence, score as words (Strong / Probable / Weak), lane.
3. **Album review screen**: your tags and MusicBrainz tags side by side, track-by-track mapping with a highlighted difference (title changed, track moved), candidate list (other editions, with country/format/year/track count) to switch with one tap, **Search** (artist + album text), **Paste MusicBrainz link/ID**, **Approve** (apply now, honouring dry run), **Keep my tags and file it** (move with minimal clean-up), **Skip for now**, **Not music/Discard** (to `_Trash`, with confirmation), and a mapping editor for the odd track.
4. **Done list** with **Undo** and what changed.
5. **Quick settings** only: pause, dry run, check now, threshold per lane (slider with the golden-set precision text if available). Everything else (lanes, templates, API keys) stays on the plugin's Emby page, like Hall Pass, because those change what is written/sent out.
6. **Home card** "Label Maker: N albums need review" (admin profile only).
7. **Plugin manager** needs no change (it already lists plugins from the GitHub updater); version gate `LabelMaker.MIN_PLUGIN`.

**Notifications (decided 2026-10-13: same mechanism as Hall Monitor, no outside service):**
- **Primary: push over the Emby websocket.** The plugin sends a message (e.g. `LabelMaker.NeedsReview` with the count and album ids) to the administrators' sessions, exactly like Hall Monitor's limit alerts. The app's existing `AlertService` (the optional foreground service that holds the live connection) receives it and posts a local notification ("3 albums need review", channel "Music ingest", generic lock-screen text, tap opens Library > Label Maker). The same connection and the same permanent notification serve both features; the settings switch becomes "Background alerts" covering limits and music review.
- **Backup: polling.** When the service is not running, a WorkManager check every 15 minutes (platform minimum) reads `GET /LabelMaker/Status` and notifies if `needsReview` grew. Off unless background alerts are on; de-duplicated by album id so you are told once.
- Honest limits: the websocket push is fire-and-forget and only reaches sessions that are connected, so a missed push is caught by the next status read (opening the app, or the poll). Android only lets an app receive it while a process is alive, which is why Hall Monitor already uses a foreground service; there is no way around that without Google's push service.
- The plugin may also add the event to Emby's own notification bell (research item). **No ntfy/Gotify/webhook**: dropped from the plan. (I had added it only for the case where the phone app is not running at all; you do not want an outside service, and the above covers it.)

**Update 2026-10-14:** the plugin side of these notifications is specified in `docs/NOTIFICATIONS.md` (events, a catch-up log with sequence numbers, a status route and a catalog route so the app can build its rule screen from the server), and the app side is built in a separate Embidextrous session against that file. See section 20 below.

**Phone-download workflow**: the plugin only watches folders on the server. Getting phone downloads there is outside the plugin (Syncthing, Nextcloud, an SMB share, or a download client on the server). The README should document the recommended setups; the design makes no assumption except that finished files land in the inbox.

## 10. Plugin API (draft, `/emby/LabelMaker/...`, admin only)
`GET Info` (version, API level, switches), `GET Status` (counts per state/lane, last run, preflight problems, matching version), `GET Config` / `POST Config` (secrets write-only), `POST Run` (check now), `POST Pause`, `GET Albums?State=&Lane=&Start=&Limit=`, `GET Albums/{id}` (files, current tags, candidates with track mappings and scores, reason), `POST Albums/{id}/Choose` (candidate release MBID + version), `POST Albums/{id}/Search` (text), `POST Albums/{id}/Resolve` (paste MBID/URL), `POST Albums/{id}/Mapping` (manual track mapping), `POST Albums/{id}/Apply`, `POST Albums/{id}/KeepTags`, `POST Albums/{id}/Skip`, `POST Albums/{id}/Discard`, `POST Albums/{id}/Undo`, `GET Cover/{releaseMbid}?size=` (cached proxy), `POST Preflight`, `POST Test/Lookup` (try a match without moving anything, like Hall Pass "Try it"). Every mutating call carries the album version to reject stale actions.

Added 2026-10-14 for push notifications (details in `docs/NOTIFICATIONS.md`): `GET Info` (plugin version, `ApiLevel`, features), `GET Events?After=<seq>&Limit=` (the catch-up log of the last 500 events), `GET Notifications/Catalog` (events, parameters, suggested default rules, lock-screen texts). `GET Status` also carries `LatestSeq`, per-lane problems and the MusicBrainz service state. Optional with the tab: a subscribed `LabelMakerStatus` websocket channel like Hall Monitor's.

## 11. Code layout (mirrors Hall Pass)
```
label-maker-emby/
  src/LabelMaker.Core/      # no Emby references: models, tag read/write (TagLibSharp), MusicBrainz + CAA + AcoustID clients,
                          # cache, rate limiter, normaliser, scorer, assignment solver, planner, sanitiser, file mover, state store
  src/LabelMaker/           # Emby shell: Plugin, config page (HTML/JS, tabs: Dashboard, Review, History, Try it, Settings),
                          # IServerEntryPoint worker + IScheduledTask, API services, library refresh, websocket push + status
  tests/LabelMaker.Tests/   # unit + golden-set regression with recorded MusicBrainz fixtures + generated audio fixtures
  docs/ (HANDOFF.md, releases/), CLAUDE.md, README, SECURITY, TESTING, CI + release workflow copied from Hall Pass
```
Tag writing is shared with the planned **Tag Team** bulk tag editor: build the writer once as its own library (`TagWriter`), reuse it in both. Review of a *manual* edit in Label Maker is effectively a one-album Tag Team feature, so keep the boundary clean.

## 12. Research spikes (Phase 0, before committing to the design)
1. **TagLibSharp in an Emby plugin**: does the DLL load next to the plugin in Emby 4.10 on Windows and Linux/Docker; license (LGPL-2.1: ship as separate unmodified DLL, state it in the README). Fallback: ffmpeg `-metadata` rewrite (loses art/format fidelity, last resort).
2. **fpcalc availability**: what OS/Docker image is the real server (unknown to me; ask the owner)? Is `fpcalc` present or installable; can Emby's bundled ffmpeg emit Chromaprint (`-f chromaprint`) (only if built with it); is a managed Chromaprint port acceptable. Decide whether fingerprinting ships in v1 or v1.1.
3. **Emby APIs**: trigger a scan of one folder (`ILibraryMonitor`/`ILibraryManager` options), enumerate music library folders for the preflight warning, background `IServerEntryPoint` lifecycle, whether plugins can send Emby notifications.
4. **MusicBrainz live probe**: real queries for ~20 of the owner's albums to see score distributions and edition ambiguity; confirm search query shapes and response sizes.
5. **Golden-set build** (section 5.5) from the owner's library; decide thresholds from the measured precision.
6. **Picard parity**: read the owner's Picard settings (naming script, ID3 version, cover art settings, which tags it writes/preserves) so default output is indistinguishable from what they have today. Needs the owner's `Picard.ini` or a screenshot of the naming script.
7. **App**: confirm WorkManager is acceptable (adds the dependency), and how the app stores per-feature switches.

## 13. Build phases and checks
| Phase | Deliverable | Done when |
|---|---|---|
| 0 | Spikes above; decisions recorded in `DECISIONS.md`; repo created by the owner (no `gh` here), name chosen | Each spike has a written answer; open questions closed |
| 1 | `Core`: tag read/write for MP3/FLAC/M4A/Ogg with round-trip tests, sanitiser, normaliser, scorer + assignment solver, MusicBrainz client with cache/limiter (fixtures) | Golden-set precision measured; tests green |
| 2 | Pipeline: stable detection, grouping, planner, applier with journal, undo, cross-volume move, dry run | End-to-end on a scratch folder, crash-recovery test (kill mid-move) |
| 3 | Plugin shell: worker, scheduled task, config + settings page (lanes, preflight), API, library refresh | Runs on the test server in dry run; page validated with the browser's own form validation (lesson from Hall Pass 1.0.2) |
| 4 | Plugin review UI: dashboard, review, history, try it | Manual review works from the Emby page |
| 5 | Cover Art Archive + push notifications to the app (events, catch-up log, catalog: `docs/NOTIFICATIONS.md`); AcoustID if the spike allows | Live check with a throw-away administrator websocket (pattern in `docs/spikes/ws-test.ps1`), then with real downloads |
| 6 | Embidextrous: Label Maker tab, review screens, Home card, WorkManager notification, version gate, unit tests | Emulator check against the test server (recipe in Embidextrous `docs/HANDOFF.md`) |
| 7 | Trial: dry run on the real inbox for a week, tune thresholds, then enable auto-apply on one lane | Zero wrong auto-applies in the trial |
| 8 | Docs, SECURITY/privacy text, release notes committed, then tag only on "publish it" | CI + CodeQL green on the tagged commit |

**Test strategy**: generated tiny audio files in each format (no copyrighted audio in the repo); MusicBrainz fixtures recorded once; property tests for the sanitiser (never empty, never over length, never escapes the root); fault injection for the mover (disk full, permission denied, process killed); the golden set for matching precision; throw-away users and folders only on the test server, cleaned up afterwards.

## 14. Risks, ranked
1. **Wrong auto-tag of a large album** -> strict auto rules, margin rule, dry run first, undo, tag backup, precision target measured on real data.
2. **Edition ambiguity** (deluxe, remaster, regional) -> release-group grouping + preference rules + show editions in the review card.
3. **fpcalc/TagLibSharp not usable in the owner's server environment** -> Phase 0 spikes decide scope before building.
4. **Permissions/path mapping in Docker** -> preflight with plain fixes.
5. **Data loss on move** -> verify-then-delete, no overwrite, trash folder, journal.
6. **MusicBrainz rate limit or outage** -> queue, cache, mirror option, back-off.
7. **Notification latency/permissions on Android** -> websocket push through the existing foreground service, polling backup, honest wording that a push can be missed.
8. **Scope creep** (classical, archives, auto-tagging existing library) -> explicit v1 non-goals below.

## 15. Not in v1 (written down so it does not creep)
Re-tagging the existing library (that is Tag Team / a later "audit" mode), classical work/movement handling, archive extraction, audiobooks/podcasts, lyrics fetching (Hall Pass has it), genre inference, AcoustID submission back to the community, multiple matching sources besides MusicBrainz (Discogs could be added later behind the same interface).

## 16. Owner answers (2026-10-13) and what they change
Answers: Windows 11 server; downloads come from **MusicGrabber** (https://gitlab.com/g33kphr33k/musicgrabber, three Docker instances writing to `E:\Downloads\Music`, `E:\Downloads\KidsMusic`, `E:\Downloads\SharedMusic`); Picard naming script supplied; inboxes are outside Emby; one Emby music library with four folders (main, kids, parents, shared) with per-user folder access set in Emby; no match = stay in the inbox; notifications through Emby/Embidextrous, no outside service; new name wanted.

### 16.1 Windows server (no Docker for Emby)
- **Mapped drive letters are per logon session.** If Emby runs as a Windows service it cannot see `V:\`. The library paths must be used as Emby itself stores them (it shows UNC paths underneath), and the account Emby runs as needs write permission on the shares. The preflight must test with the same identity Emby runs under and say exactly which share or NTFS permission is missing. Lane paths accept UNC (`\\<server>\<main-share>`) and drive letters; UNC is recommended.
- **`E:` (local) to a network share is always a cross-volume move**: copy, verify (size + hash read back over SMB), then delete the source. Atomic rename is only available within a volume. Expect slow first moves of big FLAC albums; progress is shown per album.
- **Path length**: keep full destination paths under about 240 characters (260 limit unless long paths are enabled); SMB adds no extra, but the sanitiser must count the share path.
- **Files locked** by MusicGrabber, antivirus or Emby: retry with back-off, never fail an album on a single transient lock.
- **Case-insensitive file system**: before creating `Artist/` look for an existing folder that matches ignoring case and accents (`Beyonce` vs `Beyoncé`, `ACDC` vs `AC_DC`) and reuse its exact spelling, so the library does not split one artist into two folders. Setting on by default; the new folder name comes from the template only when no match exists.
- **Docker Desktop writes into `E:` through its Linux layer**: change events may not fire; this is another reason the poll is the real trigger and `FileSystemWatcher` is only a nudge.
- The four-folder library means one Emby library, so a scan request targets the destination folder (`ReportFileSystemChanged`-style) rather than the whole library; per-user visibility is preserved because each lane writes into the folder that user group may see.

### 16.2 MusicGrabber (read from its README, not yet from a live install)
- Output layout: `Singles/Artist/Title.flac` and `Albums/Artist/Album[ (Year)]/NN - Title.ext`, multi-disc as `02-01 - Track`. So an inbox holds two kinds of input: **albums in `Albums/Artist/Album/`** (album unit = that folder) and **loose singles in `Singles/Artist/`** (unit = one track).
- **It already tags** (AcoustID + MusicBrainz, Cover Art Archive, optional ReplayGain, playlist names in Comment, lyrics as `.lrc` files, `cover.jpg`, an `.albuminfo` sidecar when the source was a MusicBrainz album link). So incoming tags are often decent: Label Maker's job is mostly to **normalise to your Picard output**, add the MBIDs, fix the file names and route to the right library. The `.albuminfo` sidecar (if it carries a release ID) is a direct match hint: read it first, verify against the track list, skip searching.
- Sources vary (FLAC from Monochrome, AAC `.m4a` from JioSaavn, Opus/MP3 via YouTube/SoundCloud): format handling must cover FLAC, M4A, Opus, MP3 from day one.
- Things to ignore or respect inside the inbox: `.trash/` (dot folders), M3U playlists and the playlists sub-folder (leave alone, report "playlist files not moved"; moving tracks would break their relative paths), `.albuminfo`, and files MusicGrabber is still copying in ("copied into /music" after validation, so a stable-time wait is still needed).
- **Interaction risks**: (1) when Label Maker moves files out of the inbox, MusicGrabber's own library index and its duplicate/"track upgrade" checks no longer see them and may download the track again: either point its duplicate checks at the real library (it supports Navidrome/Jellyfin/Plex/Lidarr lookups, not Emby, so check what it can do) or accept and test it; (2) MusicGrabber's "queue retagging" must not be used on files after they have left; (3) `FILE_PERMISSIONS`/`PUID` decide file ownership, so Label Maker must be able to move files it did not create; (4) a possible tidier hand-off later: MusicGrabber's `AUTO_IMPORT_DIR` or a webhook could tell Label Maker "album complete", removing guesswork about when a download is finished (research item; polling plus stability remains the default).
- Singles policy (decided): a single track matched to a MusicBrainz recording is filed under the release the file already claims (album tag) if that release contains the recording, otherwise under the best release by the preference rules, and goes to review when the choice is not clear. No separate Singles folder.

### 16.3 Lanes for this setup (confirmed 2026-10-13)
| Inbox | Library folder |
|---|---|
| `E:\Downloads\Music` | `\\<server>\<main-share>` |
| `E:\Downloads\KidsMusic` | `\\<server>\<kids-share>` |
| `E:\Downloads\SharedMusic` | `\\<server>\<shared-share>` |
| (a parents' inbox will be added later) | `\\<server>\<parents-share>` |

**Lanes are fully editable at any time: add, edit, delete.** The settings page has a lane list with Add, Edit (name, inbox, library folder, auto-apply, thresholds, template) and Remove. Rules:
- A lane's paths are validated and preflighted on save.
- Changing or removing a lane never touches files already in either folder.
- Remove is refused, with the reason, while that lane has an album in `Applying`. Albums waiting in review for a removed lane are kept and shown as "lane removed: choose another lane or keep in inbox".
- Two lanes may not share an inbox, and an inbox may not be inside, or contain, another lane's inbox or any library folder.
- The lane list is readable from Embidextrous. Editing stays on the plugin page for now (paths and permissions are better set on the server); an editor in the app is an option later.

### 16.4 Decisions made
- Server: the Windows PC that hosts Emby. Emby runs as the tray app under a Windows user account, so it runs in that interactive session. Drive letters such as `V:` normally work for a tray app, but disappear if the session logs off or the mapping is not reconnected, so lanes should still use the UNC form; the preflight tests with the actual account.
- Since the share is served by the same machine, the library folders probably live on a local disk behind the share. An optional per-lane "local path of the library folder" lets the move go straight to the disk (atomic rename when it is the same volume as the inbox, otherwise a fast local copy) while Emby still sees the files through its configured UNC path. To be tested; the default is the UNC path with copy, verify, delete.
- Matching: text matching in v1. AcoustID fingerprinting is a later version (MusicGrabber already fingerprints).
- Singles: filed under the release MusicBrainz says the track belongs to (Picard behaviour); no "Singles" folder.
- No match: stays in the inbox, listed in review.
- Notifications: websocket push with a polling backup; no outside service.
- Name: Label Maker.

## 17. Name (chosen: Label Maker; the candidates, for the record)
| Name | Why |
|---|---|
| **Label Maker** | A record label plus a label printer: it puts proper labels on music. Clear and funny. |
| **Roadie** | Carries the gear to the right stage and sets it up; this one carries albums to the right library. Short, friendly. |
| **Liner Notes** | What a tagger writes: the credits and details on the sleeve. |
| **Picardy** | A "Picardy third" is the major chord that resolves a minor piece; a wink at Picard. Might read as a Picard clone. |
| **Fine Tune** / **Tune Up** | Plain and clear. |
| **Track Record** | A good one is what you want in your library; pun on "track". |
| **B-Side** | The other side of the download. |
| **Sound Check** | It checks everything before the show (the library). |
| **Tag Along** | Follows the downloads and tags them. |
"Tag Team" stays reserved for the bulk tag editor. My pick: **Label Maker** (the pun does the work), runner-up **Roadie**. Before choosing, check the name is free as a GitHub repo, an Emby plugin name and a DLL name.

## 18. Phase 0 changes to this design (details in `PHASE0.md`)
- **Partial albums are the normal case** (MusicGrabber downloads track by track): subsets match; there is no track-count requirement. Rule 2 in section 5.4 now reads "every file maps to a distinct track of the release"; the setting "allow missing tracks" defaults to unlimited; the track-count score applies only to folders that look complete.
- **Matching order**: release-group search, then browse the group's official releases with recordings, then verify the files' titles and track numbers against each edition; recording search is only a fallback. Official releases only. Check several release groups when titles repeat.
- TagLibSharp is embedded in the plugin DLL and loaded by an `AssemblyLoadContext.Default.Resolving` handler (a separate DLL fails inside Emby). LGPL notice in the README.
- Always strip ID3v1 and APE on MP3 save; force ID3v2.3 when configured.
- Emby shows a new album about 1.5 to 2 minutes after the scan request; the UI says "added, Emby will show it shortly".
- The admin push works for administrator user sessions only; the app is already one.
- The MusicBrainz client needs a persistent queue, `Retry-After`, back-off and a response cache (503 seen in testing).
- The MusicGrabber sample arrived (`.albuminfo` is JSON with a `release_mbid`: use it as a direct match hint; see PHASE0 section 4). Still needed from the owner: Picard's `Picard.ini`.

## 19. New requirement: configurable notifications across Embidextrous (owner, 2026-10-13)
Applies to the whole app: Hall Monitor first, Label Maker next, any service that later gets notifications.
- **Settings > Notifications**: one section per service or plugin that has notifications (today Hall Monitor; Label Maker when built; Hall Pass and others only when they get notifications). Each section lists **rules**, as many as wanted: add, edit, delete, switch on or off.
- A rule is service + event + parameters + delivery options. Hall Monitor examples: "when a user has used X% of their time" (X set per rule, e.g. 50, 80, 90; daily or weekly limit; all users or chosen users), "when a user has used all of their time" (limit reached, keeps the +15/+30/+60 buttons). Label Maker examples: "when N or more albums need review", "when an album fails", "when a lane has a problem", "when an album is tagged automatically".
- **The existing Hall Monitor "limit reached" alert becomes the first default rule**, so today's behaviour is unchanged and the old switch migrates into it.
- Rules are stored on each phone (DataStore). A pure Kotlin, unit-tested rule engine takes the live status updates the app already receives and decides what fires. Requirements: fire once per user, per rule, per period; reset on a new day, on a Reset, or when added time lowers the percentage; no duplicate after a reconnect; state survives a restart.
- Each service declares its events and parameter types in one registry, so the settings screen is generic and a new service adds events without new UI.
- Delivery per rule: on or off, priority/sound (one Android notification channel per service), generic lock-screen text, optional quiet hours.
- Hall Monitor percentage rules need used and allowed seconds per user in the status already pushed; to be checked against the live status fields (the plugin may need no change).
- The mechanism stays: the existing `AlertService` websocket connection, polling as a backup.
- Build this as its own Embidextrous phase before the Label Maker tab, because Label Maker alerts will use it. Also add it to Embidextrous `docs/plan.md` and `decisions.md` when work on it starts.

## 20. Push notifications built into the plugin (owner request, 2026-10-14)
The owner wants push notifications built into the plugin, similar to Hall Monitor and Hall Pass, so that the Embidextrous session can hook into them and provide the settings. Decision: the plugin owns the **events** and a machine-readable **catalog**; the app owns the **rules and settings** (per phone, as decided on 2026-10-13). Transport and limits are as in section 9 (Emby websocket to administrator sessions, fire-and-forget, no outside service). The contract, the payloads, the routes, the batching rules, the privacy rules (no file paths or share names ever in a payload), where each setting lives, what the app session must do and the plugin-side build and test plan are in **`docs/NOTIFICATIONS.md`**, with sample payloads in `docs/reference/notification-samples.json`. Build order: routes and sender with Phase 3, event log, aggregator and live check with Phase 5, the app with Phase 6. Open questions for the owner are listed in `NOTIFICATIONS.md` section 10.