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
