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
