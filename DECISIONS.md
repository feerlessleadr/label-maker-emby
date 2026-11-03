# DECISIONS: Label Maker

Append-only. Newest at the bottom. Never rewrite an entry; add a new one that references it.
The complete dated log (sections 11 to 37) is in `docs/HANDOFF.md`, and section 0 there lists every owner decision. The entries below are pointers to the standing ones, so a new session can search here first.

### 2026-10-13, Plugin with an Emby-free engine, named Label Maker
- **Context:** The owner wanted to stop the manual routine of downloading, Picard lookup, tagging and moving files.
- **Options considered:** Standalone program; Emby plugin; Emby plugin with a separate engine.
- **Decision:** An Emby plugin over an Emby-free `LabelMaker.Core`. Names fixed: `LabelMaker.dll`, GUID `a29bf822-41ab-4e14-a814-a8d386e7f745`, routes `/emby/LabelMaker/...`, settings `LabelMaker.xml`.
- **Rationale:** The plugin gets the server's library, sessions and websocket. The engine stays testable and could be wrapped later.
- **Impact:** Layout copied from `C:\dev\hall-pass-emby`. Source of the design: `C:\dev\emby-tagging\label-maker-design.md`.

### 2026-10-13, Lanes, dry run, naming and no-match behavior
- **Decision:** Lanes (inbox folder to library folder) are fully editable; deleting one never touches files. Dry run is the default. Naming is Picard's stock script exactly. ID3v2.4, UTF-8, ID3v1 kept. No Singles folder (a single is filed under its album). No match means the album stays in the inbox and is listed for review. Nothing is ever deleted (`_Trash`, `_Replaced`, `_Duplicates` live inside the inbox).
- **Rationale:** The owner's current Picard output is the standard, and nothing may be lost.
- **Impact:** `docs/MATCHING-RULES.md` (keep in step with the code).

### 2026-10-13, Notifications stay inside the plugin
- **Decision:** Emby websocket push to the app, polling only as a backup, no outside service (no ntfy, Gotify, webhooks, Pushover). Rules and quiet hours live in the app. What the server sends lives on the plugin page. No file path or share name ever appears in an alert.
- **Rationale:** Owner's privacy choice.
- **Impact:** `docs/NOTIFICATIONS.md`, Embidextrous `docs/notifications-plan.md`.

### 2026-10-13, AcoustID fingerprinting deferred
- **Decision:** Text and MusicBrainz id matching only in v1.
- **Rationale:** MusicGrabber already fingerprints.

### 2026-10-13, Thresholds and edition rule
- **Decision:** 0.92 auto, 0.60 review, 0.5 per file, margin 0.05; target 99% precision for automatic filing. Edition rule default is "same titles"; strict "same recordings" is a setting. Two different MusicBrainz albums with the same title that both fit go to review.
- **Rationale:** Set from calibration on 287 real albums (`docs/CALIBRATION.md`).

### 2026-10-14, Plugin pushes reach the app by websocket subscription
- **Context:** `SendMessageToAdminSessions` reaches only sockets whose token is in the address. The app keeps its token in a header.
- **Decision:** `LabelMakerEventsStart` with the admin token in the message body, checked through the session manager in `Services/SocketAuth.cs`, never logged. Later (0.1.11.0) `LabelMakerStatusStart` works the same way.
- **Impact:** The app must never be edited from this repo. Change `docs/NOTIFICATIONS.md` and tell the owner.

### 2026-10-14, Release checklist after a broken release
- **Context:** v0.1.1.0 shipped with a page script that did not parse, so the menu item did nothing.
- **Decision:** Before any release run `tools/Check-PageScript.ps1` and the live test. Click through the page by hand only if the page files changed. After release, download the published DLL and rerun the live test with it. Never tag first and test later. Releases only when the owner says "publish it".

### 2026-10-19 to 2026-11-03, Decisions from the trial sessions
- **Decision:** Details and reasons are in HANDOFF sections 31 to 37.
  - Turning dry run off re-checks Ready albums and files them by the same rules (31).
  - Cover art is a `cover.jpg` file only, never embedded, never replacing an existing one, on by default (32).
  - Automatic passes never overwrite library files. A person picks keep both, replace or file only the new files, and each is undoable (33, 35).
  - Tracks that arrive later in a filed folder are a new batch with their own undo record (33).
  - Song-title search runs only after album-name searches fail, tries at most three titles, and can never file automatically (34).
  - Classical music, audiobooks and podcasts are held for review, never filed automatically, have no setting and are not moved (36).
  - The status channel takes the token as the third start value, polls a cheap version once a second, and its heartbeat resends the whole status (37).
- **Impact:** Released in 0.1.6.0 through 0.1.11.0.

### 2026-11-03, Keep private details out of tracked files
- **Decision:** The owner's LAN IP, share names, Windows user name, work email and Picard.ini live only in git-ignored `docs/local/`. Tracked docs use neutral examples (`D:\Music Inbox`, `\\nas\music`). Commits are authored `feerlessleadr`.
- **Rationale:** The repo is public. History was rewritten once for the LAN IP and share names.

### 2026-10-19, Ready albums are re-checked when dry run is turned off
- **Context:** The owner's first trial report: two albums showed "Ready (dry run)". After dry run was switched off, "Check inbox now" left them in Ready; the owner had to open each album and approve it by hand. Cause: the worker only handled albums in the Waiting state, and nothing moved Ready back.
- **Options considered:** An "approve all Ready" button; re-check Ready albums on the next pass; tell the owner to approve by hand.
- **Decision:** With dry run off, every Ready album that no person decided about goes back to Waiting and is filed by the same rules as a new album (it asks MusicBrainz again, which the disk cache makes cheap).
- **Rationale:** `docs/INSTALL-TRIAL.md` promised that matched albums are filed by themselves once dry run is off. No new screen, and the same safety checks apply.
- **Impact:** Released in 0.1.6.0. `InboxWorker`, HANDOFF section 31.

### 2026-10-19, Cover art is a file in the album folder, not embedded in the tags
- **Context:** Roadmap item "cover images for albums that have none". The owner's Picard embeds one 500 px front image and saves no file.
- **Options considered:** Embed like Picard; save `cover.jpg`; both.
- **Decision:** After an album is filed, if its library folder has no `cover`, `folder` or `albumart` image, download the release's front cover (500 px, release only, no release-group fallback) from the Cover Art Archive and save `cover.jpg` or `.png`. Never replace an existing cover. No retry and no alert when the archive has nothing or is unreachable. On by default, with a switch and a privacy sentence next to it. Undo removes only the cover Label Maker added.
- **Rationale:** Emby reads the folder image; leaving the audio tags alone keeps Undo simple and the files untouched.
- **Impact:** Released in 0.1.7.0. Only the release id goes to `coverartarchive.org` (SECURITY.md, README, settings page updated). Embedding stays a possible later option. HANDOFF section 32.

### 2026-11-02, Surprising discovery: tracks arriving later in a filed folder were never filed
- **Context:** Found while wiring the duplicate live checks. An album's id is a hash of the lane and the folder, and `Applier.Apply` answered "Already applied." whenever an earlier finished record existed for that id. MusicGrabber delivers an album track by track, so later tracks were marked filed but stayed in the inbox (0.1.7.0 and earlier). My first test for it failed for the wrong reason (the extra track title was not on the fixture release); after fixing the test I confirmed the three new tests fail with the fix switched off.
- **Options considered:** Merge the new files into the old record; archive the old record and chain it; change how ids are made.
- **Decision:** A plan whose files are still in the inbox is a later batch. The old record is archived under `<id>-<time>` and linked by `PreviousJournalId`; Undo steps back one batch at a time. A rescue rule re-checks any album marked Done while audio files are still in its inbox folder, which also frees albums stuck by the old behavior.
- **Rationale:** Merging could put the same inbox path in two entries and make Undo collide; changing ids would orphan queue items.
- **Impact:** Released in 0.1.8.0 (the notes tell the owner to update). `Applier`, `JournalStore`, `InboxWorker`; HANDOFF section 33.

### 2026-11-02, Duplicates: never automatic, three explicit choices, nothing deleted
- **Context:** One file with a name already in the library made the whole album wait as "Already in library". `docs/DESIGN.md` had promised keep both (suffix) and replace.
- **Options considered:** An automatic "better quality wins" rule; numbered suffix versus a separate folder; replace by overwriting versus by backup; for a partly overlapping album, leaving the duplicates in place versus setting them aside.
- **Decision:** Automatic passes still refuse. A person chooses on the preview: keep both (the new copy is numbered "(2)", lyric files follow); replace (every old file is first moved to `<inbox>/_Replaced/<album id>/...` after all new files are tagged; Undo restores them and refuses if a saved file is missing); or file only the new files (the duplicates are set aside in `<inbox>/_Duplicates/<album id>/...`). The preview shows both file sizes; there is no automatic quality rule.
- **Rationale:** Nothing is deleted and every choice is undoable. Set aside rather than left in place, because a left-over audio file would loop through the "Done but audio still in the inbox" rescue forever.
- **Impact:** Released in 0.1.8.0 and 0.1.9.0. API `OnDuplicate` on Preview and Apply; the scanner ignores folders whose names start with an underscore. HANDOFF sections 33 and 35.

### 2026-11-02, Song-title search is a last resort and never automatic; a ranking tweak was tried and removed
- **Context:** About 18% of full albums still waited for a person, and some have a wrong or missing album tag.
- **Options considered:** Search MusicBrainz recordings by title and artist; AcoustID fingerprinting (deferred by the owner); promoting the result to automatic when several titles agree.
- **Decision:** Only after every album-name search fails (or the album tag is missing), search at most three song titles (hit score at least 80, title at least 0.85 alike, artist plausibly the same), count which albums hold them, check up to five albums track by track, and always send the result to review with a plain sentence. The review Search button does the same when what was typed fits nothing. A single title with no artist is not searched.
- **Rationale:** With the album name gone, a few matching titles are the only evidence, so a person decides.
- **Impact:** Released in 0.1.9.0. Measured on 40 real albums with the name removed: all 40 reached review and the right album was first for 30; the 10 misses were single songs that sit on dozens of albums. **Failed attempt:** a tie-break "prefer the album named after the song" changed nothing (still 30 of 40), so it was removed. HANDOFF section 34.

### 2026-11-02, Classical music, audiobooks and podcasts are held for review, with no setting
- **Context:** `docs/MATCHING-RULES.md` already promised that such albums are never applied automatically; the rule was not built.
- **Options considered:** A setting to turn the safety net off; moving such albums to another folder; leaving audiobooks and podcasts completely alone (the DESIGN text).
- **Decision:** Plain rules (genre words, chapter-like titles, work-and-movement titles, or any file over 90 minutes; the composer tag alone does not count). A recognized album is never filed automatically and shows a plain sentence; the matcher still runs, so candidates are shown. No setting, nothing moved.
- **Rationale:** A confident-looking match is most likely wrong for these albums; review is harmless and one click to skip or discard.
- **Impact:** Released in 0.1.10.0. Genre and composer tags are now read (never written). Only 1 of 322 exported albums was flagged, but the old exports have no genres, so the genre rules are unmeasured. I did not re-scan the owner's library from this session; the export script now writes genre and composer and `tools/LabelMaker.Calibrate --media-only` measures it. HANDOFF section 36.

### 2026-11-03, Status websocket channel for the app (an interface change) and a `Status.Version` bug
- **Context:** Roadmap item for the Embidextrous Library tab. Writing the live check showed that `Status.Version`, documented as growing with every change, ignored pause, dry run, the lane list and MusicBrainz health.
- **Options considered:** Hook every place that changes state versus poll a cheap version; send an empty heartbeat versus the whole status.
- **Decision:** `LabelMakerStatusStart` with `"0,30000,<admin token>"` (the token is the third value; there is no `afterSeq`); reply `LabelMakerStatus` with exactly the body of `GET Status`, on subscribing, within about a second of every change, and as a 30 second heartbeat that resends the whole status. The administrator check moved to a shared `SocketAuth`. `Info.Features` gained `"statuspush"`. The engine polls a cheap `StatusVersion()` once a second, and the version now also grows when pause, dry run, the lane list or MusicBrainz health changes.
- **Rationale:** Polling cannot miss a change; resending the status gives the app current numbers after any gap.
- **Impact:** Released in 0.1.11.0. **The Embidextrous session must be told** (`docs/NOTIFICATIONS.md` section 13; the app repo was not touched). Anything comparing `Status.Version` now sees settings changes. HANDOFF section 37, WORKSPACE.md connection 7.

### 2026-11-02, Test tooling lessons: the Windows prompt, MusicBrainz busy, the browser cache, and a permission boundary
- **Context:** Several failures that were not product bugs. Private Emby instances stopped starting on 2026-11-02: Emby logged "Requesting administrative access to authorize http server" and waited for a Windows elevation (UAC) approval that only counts while that start is waiting (about three minutes). A live check failed because MusicBrainz answered "busy" for longer than the plugin waits (the plugin correctly left the album waiting). The browser pane ran an older plugin script from its HTTP cache under the same URL, and an older page timer printed an error every five seconds (found in the console; fixed in 0.1.8.0). The pane cannot answer `window.confirm`.
- **Decision:** Start the live test and send a desktop notification at the same moment so the owner can approve the prompt. Live checks that depend on MusicBrainz ask again up to three times and print a skip instead of failing. For a click-through: a new tab per build, refresh the cached script with `fetch(url, {cache: 'reload'})`, check one string that changed, answer `confirm` through the script tool and say so. Do not kill processes or sweep the temp folder by wildcard: the permission checker refused, so I asked the owner and later removed only my own leftover folders by exact name.
- **Impact:** HANDOFF section 0 traps 13 to 15, `TESTING.md`, `CLAUDE.md`. No product change except the timer fix.

### 2026-11-03, Handoff discipline for a seamless new session
- **Context:** The owner asked that all decisions be recorded so a new Claude instance can take over.
- **Decision:** Section 0 of `docs/HANDOFF.md` is rewritten after every release (state, release history, what is unverified, every decision, owner to-dos, roadmap and unasked ideas, how to resume and release, traps, index of the log); every decision goes in the dated log in the same commit as the change; a paste-ready `docs/local/next-session-prompt.md` and two memory notes point to it. Commit trailers follow the owner's instruction (`Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`) over differing suggestions in system reminders.
- **Impact:** `CLAUDE.md` "Record everything" rule; HANDOFF section 0.
