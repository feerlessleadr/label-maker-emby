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
`tools/Test-LabelMakerPlugin.ps1` deploys the built `LabelMaker.dll` to a local Emby test server, runs 35 checks (routes, settings validation, a throw-away administrator websocket for the phone alerts, a dry run, approving, undo, discard, pause, a broken lane and its recovery, real filing, alert switches) with real tagged files and the real MusicBrainz, and then removes everything it made and leaves the server as it found it. Build first (`dotnet build src/LabelMaker -c Release`). The script's header says what it needs (the admin API key in `%USERPROFILE%\.emby-spike-key`, network access). It is not run by CI.
