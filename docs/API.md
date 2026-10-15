# Label Maker plugin routes (`/emby/LabelMaker/...`)

Plugin 0.1.1.0, `ApiLevel` 1. Every route needs an **administrator** token (`X-Emby-Token`); a request without one is refused. Bodies are JSON. Field names are exactly as written. Nothing in an answer is a file path except the library-relative `Destination` and the inbox-relative `Folder` of an album (names of folders, never a drive or share). The phone alerts have their own contract: `docs/NOTIFICATIONS.md`.

## Plugin and alerts
| Route | Answer |
|---|---|
| `GET Info` | `{ PluginVersion, ApiLevel, Features: ["events","catalog","status","albums","review"] }` |
| `GET Status` | `{ ApiLevel, Version, ServerTimeUtc, Paused, DryRun, LastPassUtc, MatchingVersion, Counts, Lanes, Service, LatestSeq }`. `Counts` has `Waiting, NeedsReview, NoMatch, Ready, Applying, Done, Failed, Duplicate, Skipped`. `Lanes[]` is `{ LaneId, LaneName, Counts, Problems: [{ Kind, Message, Blocking }] }`. `Service` is `{ MusicBrainz: "Ok"\|"Trouble", SinceUtc }`. `Version` grows with every change. |
| `GET Events?After=<seq>&Limit=100` | `{ LatestSeq, Truncated, Events: [{ Seq, Name, AtUtc, Data }] }`, oldest first |
| `GET Notifications/Catalog` | the alert rules (see NOTIFICATIONS.md section 5) |
| `GET Preflight` | the health check of every lane, always fresh: `{ Issues: [{ LaneId, Code, Message }], Lanes: [{ LaneId, LaneName, CanRun, Checks: [{ Name, Level: "Ok"\|"Info"\|"Warning"\|"Error", Message }] }] }` |
| `POST Run` | starts "Check inbox now" in the background: `{ Started: true }`; read `Status` afterwards |
| `POST Pause` `{ Paused: bool }` | `{ Paused }` (saved in the plugin settings) |
| `POST DryRun` `{ DryRun: bool }` | `{ DryRun }` (saved in the plugin settings) |

The websocket subscription for phone alerts (`LabelMakerEventsStart` / `LabelMakerEvents`) is described in `docs/NOTIFICATIONS.md` section 12.

Settings (lanes, thresholds, alert switches) are read and written with Emby's standard plugin settings route, `GET/POST /emby/Plugins/a29bf822-41ab-4e14-a814-a8d386e7f745/Configuration`; a saved value that breaks a rule is refused with `400` and a plain sentence as the body.

## Albums
| Route | Answer |
|---|---|
| `GET Albums?State=&Lane=&Text=&Start=0&Limit=50` | `{ Total, Start, Items: [Album] }`, newest change first. `State` is comma separated: `Waiting, NeedsReview, NoMatch, Ready, Applying, Done, Failed, Skipped, Duplicate`. `Text` matches artist, album or folder. |
| `GET Albums/{Id}` | the album summary plus `Source`, `Outcome`, `Notes` (how it searched), `Candidates` (`{ ReleaseId, Title, Artist, Country, Date, Score, WeakFiles }`) and, while the album is still in the inbox, `FileList` (`{ File, Title, Artist, Album, Track, Disc, Seconds }`). `400` when the album is no longer known. |

An **Album** summary is `{ Id, LaneId, LaneName, Artist, Album, Folder, Kind, State, Reason, Files, Held, Discarded, Score, BestReleaseId, BestTitle, BestArtist, Destination, FirstSeenUtc, UpdatedUtc }`. `Artist` and `Album` are what the files' tags say. `Reason` is one plain sentence. `Held` means a person decided and Label Maker leaves it alone until "check again".

## Review actions (all `POST`, body optional)
Each returns `{ Success, Message, State }` unless noted; `Message` is written for the person. An album being checked or filed at that moment is refused. A person's choice is carried out even in dry run mode.

| Route | Body | What it does |
|---|---|---|
| `Albums/{Id}/Preview` | `{ ReleaseId, Mapping?: [{ File, Disc, Position }] }` | What using that edition would do. Answer: `{ Success, Message, ReleaseId, Title, Artist, Date, Country, Score, Files: [{ Source, Destination, Disc, Position, Title, Score, Weak }], Problems, CanApply, IsDuplicate, Destination }`. Changes nothing. |
| `Albums/{Id}/Apply` | the same | Tags and files the album with that edition (after the same checks). |
| `Albums/{Id}/Search` | `{ Artist, Album }` | `{ Success, Message, Candidates }` from MusicBrainz, each scored against the files. |
| `Albums/{Id}/Resolve` | `{ Text }` | A pasted MusicBrainz release link, release-group link or bare id turned into `Candidates`. |
| `Albums/{Id}/KeepTags` | | Files the album with its own tags unchanged. |
| `Albums/{Id}/Skip` | | Leaves it alone until `Recheck`. |
| `Albums/{Id}/Recheck` | | Back in line for a fresh check. |
| `Albums/{Id}/Discard` | | Moves its files to `<inbox>/_Trash/...`. Nothing is deleted. |
| `Albums/{Id}/Restore` | | Moves a discarded album back. |
| `Albums/{Id}/Undo` | | Puts a filed album back in the inbox with its old tags and names. |
| `Albums/{Id}/Split` | | Puts each album of a mixed folder in a folder of its own. |

The rules behind every sentence are in `docs/MATCHING-RULES.md`.
