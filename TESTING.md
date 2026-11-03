# Testing

Run the engine tests from the repo root:

```
dotnet test tests/LabelMaker.Tests
```

The tests run offline. They use:
- `Fixtures/silence.mp3` and `Fixtures/silence.flac`: tiny silent files generated with ffmpeg (no copyrighted audio). To regenerate:
  ```
  ffmpeg -f lavfi -i anullsrc=r=22050:cl=mono -t 0.3 -c:a libmp3lame -b:a 32k -id3v2_version 4 silence.mp3
  ffmpeg -f lavfi -i anullsrc=r=22050:cl=mono -t 0.3 -c:a flac silence.flac
  ```
- `Fixtures/picard-default-naming-script.txt`: the owner's stock Picard script.
- `Fixtures/albuminfo-insomniac.json`: a real MusicGrabber `.albuminfo` sample.
- `Fixtures/mb-recording-search-real.json`: a MusicBrainz recording-search answer recorded once (song "Brat" by Green Day, 2026-11-02); `Fixtures/mb-recording-search.json` is a small hand-made one.
- `Fixtures/mb-release-insomniac-2021.json`: a MusicBrainz release answer recorded once (`/ws/2/release/46b2741f-...?inc=recordings+artist-credits+media&fmt=json`).

Test copies of files are written to the temp folder and deleted afterwards.

## Live test of the plugin (needs a local Emby test server)
`tools/Test-LabelMakerPlugin.ps1` starts a private, throw-away Emby Server instance of its own (a copy of the program folder in the temp folder, its own empty data folder and port, first-run wizard done through the API; the shared test server is never touched), deploys the built `LabelMaker.dll` into it, runs 70 checks (the page script loaded in a real browser, the events subscription connected the way the app connects, routes, settings validation, an administrator websocket for the phone alerts, a dry run, approving, undo, discard, pause, a broken lane and its recovery, real filing, alert switches) with real tagged files and the real MusicBrainz, and then deletes everything it made. Build first (`dotnet build src/LabelMaker -c Release`). It needs an unpacked Emby Server (`-ServerRoot`), network access and Google Chrome (for the page check); `-KeepRunning` leaves the instance up for trying the page by hand (`tools/Stop-LabelMakerTestInstance.ps1` stops it). `tools/Check-PageScript.ps1` runs the page-script check alone. Neither is run by CI (CI runs `node --check` on the page script).

**Heads-up (2026-11-02):** starting the private instance can make Windows ask for an administrator approval (UAC) for Emby's HTTP server; the approval only counts while that start is waiting (about three minutes), so someone at the machine must click it while the test starts. If the test stops with "The private Emby instance did not come up", look for that prompt, wait a few seconds after any earlier instance was stopped, and see `docs/HANDOFF.md` section 0, trap 13. A few live checks use the real MusicBrainz (matching, the song-title search) and ask again when it answers "busy". `tools/Check-PageScript.ps1` needs Chrome. The calibration tool (`tools/LabelMaker.Calibrate`) also needs the local library exports in `docs/local/`.
