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
- `Fixtures/mb-release-insomniac-2021.json`: a MusicBrainz release answer recorded once (`/ws/2/release/46b2741f-...?inc=recordings+artist-credits+media&fmt=json`).

Test copies of files are written to the temp folder and deleted afterwards.

## Live test of the plugin (needs a local Emby test server)
`tools/Test-LabelMakerPlugin.ps1` starts a private, throw-away Emby Server instance of its own (a copy of the program folder in the temp folder, its own empty data folder and port, first-run wizard done through the API; the shared test server is never touched), deploys the built `LabelMaker.dll` into it, runs 37 checks (the page script loaded in a real browser, routes, settings validation, an administrator websocket for the phone alerts, a dry run, approving, undo, discard, pause, a broken lane and its recovery, real filing, alert switches) with real tagged files and the real MusicBrainz, and then deletes everything it made. Build first (`dotnet build src/LabelMaker -c Release`). It needs an unpacked Emby Server (`-ServerRoot`), network access and Google Chrome (for the page check); `-KeepRunning` leaves the instance up for trying the page by hand (`tools/Stop-LabelMakerTestInstance.ps1` stops it). `tools/Check-PageScript.ps1` runs the page-script check alone. Neither is run by CI (CI runs `node --check` on the page script).
