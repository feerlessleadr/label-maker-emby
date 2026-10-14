# Label Maker push notifications: the contract for the phone app

**Status: DESIGN ONLY (2026-10-14). Nothing here is built yet.** This file is written so that the session building **Embidextrous** (the owner's Android app, `C:\dev\embidextrous`) can code against it now, using the sample payloads in `docs/reference/notification-samples.json` as test fixtures, while the plugin side is built here. If something in this file has to change, change it here first and tell the owner; the app session reads this file, it does not edit the plugin repo.

## 1. What the owner asked for
On 2026-10-14 the owner said they want **push notifications built into the plugin, similar to Hall Monitor or Hall Pass, so that the Embidextrous session can hook into them and provide the right settings.** This agrees with the earlier decisions (HANDOFF.md section 3 and DESIGN.md sections 9 and 19): the push goes over Emby's own websocket to the app's existing background service (`AlertService`), polling is only a backup, there is **no outside service** (no ntfy, Gotify, webhooks or Pushover), and per-phone rules live in the app (`C:\dev\embidextrous\docs\notifications-plan.md`).

## 2. How it works: three layers
1. **Events (the alert).** When something worth an alert happens, the plugin calls Emby's `ISessionManager.SendMessageToAdminSessions(name, data, cancellation)`. The message arrives as `{"MessageType":"LabelMaker.NeedsReview","Data":{...}}` on the websocket of every connected **administrator** session (checked live in Phase 0: a socket authenticated as an administrator received it; a socket opened with only an API key received nothing). It is fire-and-forget: a session that is not connected at that moment never sees it.
2. **Catch-up (so a missed push is not lost).** Every event gets a growing sequence number `Seq` and is kept in a small persisted log. `GET /emby/LabelMaker/Events?After=<seq>` returns what the app missed; `GET /emby/LabelMaker/Status` returns the current counts. The app uses these when it connects, when it opens, and from its 15-minute WorkManager poll when the background service is not running. Dedupe by `Seq`.
3. **Catalog (so the app's settings screen is generic).** `GET /emby/LabelMaker/Notifications/Catalog` lists every event with its title, description, parameters and suggested default rule, so the app can build "Add a rule" for Label Maker from the server's description, exactly as `notifications-plan.md` asks ("each service declares its events and parameter types in one registry"). The app may keep a built-in copy of the same table as a fallback for an older plugin.

An optional fourth piece for the Library tab, not needed for alerts: a subscribed live-status channel like Hall Monitor's `HallMonitorStatusStart` / `HallMonitorStatus` (the app sends `{"MessageType":"LabelMakerStatusStart","Data":"0,30000"}`; the plugin then sends `LabelMakerStatus` with the same body as `GET Status` whenever something changes and every 30 seconds). Built later with the tab (Phase 6); see `C:\dev\watching-eye\src\HallMonitor\HallMonitor\StatusPushListener.cs` for the pattern.

## 3. Who can receive, and how the app calls the plugin
- All routes are `[Authenticated(Roles="Admin")]`. The app uses the **signed-in administrator's token** in `X-Emby-Token` (the same as the Hall Pass tab). There is **no separate plugin key** (Hall Monitor has one for its public API; Label Maker does not).
- Only administrator sessions receive pushes. The app's `AlertService` already connects as the signed-in administrator, so it receives them.
- Version gate: `GET /emby/LabelMaker/Info` returns `{ "PluginVersion": "1.0.0.0", "ApiLevel": 1, "Features": ["events","catalog","status"] }`. Every pushed event also carries `ApiLevel`. The app should gate on `LabelMaker.MIN_PLUGIN` like `HallPass.MIN_PLUGIN` and show a "too old" card instead of failing.

## 4. The events
Every event's `Data` object starts with these **common fields**: `ApiLevel` (int), `Seq` (long, grows by 1 per event, persisted so it survives restarts), `EventId` (GUID), `ServerTimeUtc` (ISO 8601), and `Event` (the short name, for example `NeedsReview`). The message name is `LabelMaker.` plus the short name. Names and text never contain file paths or share names: albums are described by artist, album and lane name only.

| Message | Sent when | Event fields | Suggested app rule (parameters) |
|---|---|---|---|
| `LabelMaker.NeedsReview` | The number of albums waiting for a person (needs review plus no match) went **up**. At most one per minute: several albums arriving together make one event. | `Count` (albums needing review now), `NoMatchCount`, `New` (how many since the last event), `Lanes` (list of `{LaneId, LaneName, Count}`), `Albums` (the first 5 new ones: `{Id, Artist, Album, LaneId, Reason}`) | "N or more albums need review" (`MinCount`, default 1) |
| `LabelMaker.AutoApplied` | A pass finished and applied (or, in dry run, would have applied) albums by itself. One summary per pass, not one per album. | `Mode` (`"Applied"` or `"WouldApply"` for a dry run), `Count`, `Albums` (the first 5: `{Id, Artist, Album, LaneId, Files}`) | "An album was tagged automatically" (no parameters; off by default, it is chatty after a big drop) |
| `LabelMaker.AlbumFailed` | An album could not be handled and needs fixing: a damaged file, a library folder that cannot be written, a copy that did not verify. Not sent for "MusicBrainz busy" (that retries by itself). | `Id`, `Artist`, `Album`, `LaneId`, `Reason` (one plain sentence), `Retryable` (bool) | "An album failed" (no parameters) |
| `LabelMaker.LaneProblem` | A lane's preflight or a pass found a problem: `InboxUnreachable`, `LibraryUnreachable`, `NotWritable`, `LowDiskSpace`, `InboxInsideLibrary`, `PathTooLong`. Sent when the problem appears, then at most once a day while it lasts. | `LaneId`, `LaneName`, `Kind`, `Message` (plain sentence) | "A lane has a problem" (no parameters) |
| `LabelMaker.LaneRecovered` | A lane problem is gone. | `LaneId`, `LaneName`, `Kind` | The app clears the matching alert; no rule |
| `LabelMaker.ServiceTrouble` | MusicBrainz could not be reached or stayed busy for more than 15 minutes while albums are waiting. | `SinceUtc`, `WaitingCount` | "Music lookups are stuck" (`Minutes`, default 15; off by default) |
| `LabelMaker.ServiceRecovered` | MusicBrainz answers again. | none | The app clears the matching alert; no rule |
| `LabelMaker.PassFinished` | A pass that the owner started with "Check inbox now" finished (a timed pass sends nothing). | `Found`, `Queued`, `Processed`, `NeedsReview`, `Failed` | No rule; the app can show a toast or refresh the Library tab |

**Rules for sending (so nothing floods the phone):** a first pass after installing the plugin that finds hundreds of albums sends one `NeedsReview` summary, not hundreds; an album that is already counted does not send again until its state changes; while the plugin is paused it sends nothing except `LaneProblem`; `Count` always says how many need review **now**, so a missed event is repaired by the next one.

## 5. The catalog route
`GET /emby/LabelMaker/Notifications/Catalog` returns:
```json
{
  "ApiLevel": 1,
  "Service": { "Id": "LabelMaker", "Title": "Label Maker", "ChannelId": "music-ingest", "ChannelTitle": "Music ingest" },
  "Events": [
    {
      "Id": "LabelMaker.NeedsReview",
      "Title": "Albums need review",
      "Description": "Sent when downloaded albums could not be matched with certainty and wait for you.",
      "Params": [ { "Name": "MinCount", "Label": "When this many albums need review", "Type": "int", "Min": 1, "Max": 500, "Default": 1 } ],
      "DefaultRule": { "Enabled": true, "Params": { "MinCount": 1 } },
      "LockScreenText": "Music needs review. Unlock to see which.",
      "OpensTab": "LabelMaker/Review"
    }
  ]
}
```
`Type` is one of `int`, `bool`, `text`, `choice` (with `Choices`). `LockScreenText` is the generic text for the lock screen (the app's standing rule: names only after unlocking). `OpensTab` says where tapping the notification goes. A full sample is in `docs/reference/notification-samples.json`.

## 6. The status and catch-up routes
- `GET /emby/LabelMaker/Status` returns `{ ApiLevel, Version, ServerTimeUtc, Paused, DryRun, LastPassUtc, MatchingVersion, Counts: {Waiting, NeedsReview, NoMatch, Ready, Applying, Done, Failed, Duplicate, Skipped}, Lanes: [{LaneId, LaneName, Counts, Problems:[{Kind, Message}]}], Service: {MusicBrainz: "Ok"|"Trouble", SinceUtc}, LatestSeq }`. `Version` grows with every change.
- `GET /emby/LabelMaker/Events?After=<seq>&Limit=100` returns `{ LatestSeq, Events: [ { Seq, Name, AtUtc, Data } ] }` oldest first, from a log of the last 500 events kept in the plugin's data folder. If `After` is older than the log, the response says `Truncated: true` and the app should just read `Status`.
- `GET /emby/LabelMaker/Albums?State=NeedsReview&Start=0&Limit=50` and the other album routes are in DESIGN.md section 10; the app's review tab uses them. Notifications need only the three routes above.

## 7. Where each setting lives
| Setting | Lives in | Why |
|---|---|---|
| Which rules exist, thresholds, on or off, sound and priority, quiet hours, which lanes to hear about | **The app** (per phone) | Decided 2026-10-13: rules live on each phone; the app filters by `LaneId` if the owner wants a lane silent |
| "Send phone alerts" master switch (default on) and a switch per event (default on, except `AutoApplied` and `ServiceTrouble`) | **The plugin's settings page** | They change what the server sends. When off, the event is not sent and not logged |
| Pause, dry run, check now | Plugin page, and quick switches in the app | As decided for the Library tab |
| Lanes (add, edit, delete) | Plugin page; read-only in the app | Paths and permissions are best set on the server |
| Emby's own notification bell (`INotificationManager`) | Optional later, off by default | Not needed for the app; the owner does not want an outside service |

## 8. What the Embidextrous session needs to do (when it is ready to)
1. Read this file and `docs/reference/notification-samples.json`; write parser tests from the samples (tolerant parsing: unknown fields ignored, missing optional fields tolerated).
2. Add a Label Maker section to its notification-rule screens, built from the catalog (with a built-in fallback copy of section 4 for plugins that predate the catalog), one Android channel for the service (`music-ingest`, "Music ingest").
3. In `AlertService`, handle websocket messages whose `MessageType` starts with `LabelMaker.`: dedupe by `Seq`, evaluate the rules, notify with the generic lock-screen text, and clear the notification on `LaneRecovered` and `ServiceRecovered`.
4. Keep the last seen `Seq`; on connect, on app open and from the 15-minute poll, call `Events?After=` (then `Status` if truncated) so a missed push is repaired.
5. Version-gate the tab and the rules on `Info.ApiLevel` and `LabelMaker.MIN_PLUGIN`.
6. The tab itself (summary card, review list, album screen, Home card) is separate work: DESIGN.md section 9.

## 9. What is built on the plugin side, and the plan
**Nothing yet.** The plan, in the plugin's own terms:
- **Core (Emby-free, unit tested):** `NotificationEvent` and the event names and payload classes; `EventLog` (sequence numbers, ring buffer of 500, persisted JSON, tolerant of a damaged file); `EventAggregator` (turns queue state changes and lane health into events with the batching and once-a-day rules above, never including a path); the catalog definition. Tests: batching, no flood after a first big pass, no repeat for an album already counted, dry run gives `WouldApply`, paused sends only `LaneProblem`, `Seq` survives a restart, payloads contain no path or share name.
- **Plugin shell:** `PushSender` (calls `SendMessageToAdminSessions`, catches errors so a failed push never stops the worker), the routes `Info`, `Status`, `Events` and `Notifications/Catalog`, the two settings switches on the page, and (with the tab) the `LabelMakerStatus` listener.
- **Live check** (like Hall Monitor's): throw-away administrator with an authenticated websocket (pattern in `docs/spikes/ws-test.ps1`), trigger each event, check the message names and the common fields, then the catch-up route after a disconnect; delete the user afterwards.
- **Emulator check** together with the app session, after both sides exist.
- Build order: with Phase 3 (shell) the routes and `PushSender`; with Phase 5 the aggregator, the log and the live check; Phase 6 is the app.

## 10. Open questions
- Should `AutoApplied` ever be on by default? (Suggested: no, it is chatty after a big drop; the owner can add a rule.)
- Should the catalog also carry suggested quiet hours? (Suggested: no, quiet hours are the app's feature and apply to every service.)
- Per-lane silence: filtered by the app from `LaneId`, or also a per-lane switch on the plugin page? (Suggested: the app; add the plugin switch only if the owner asks.)
- The optional live-status channel (`LabelMakerStatus`): build it with the tab, or poll `Status`? (Suggested: build it with the tab, as Hall Monitor did.)
