# Calibration: what the owner needs to do

Label Maker's thresholds (auto-apply 0.92, review 0.60, margin 0.05) are starting values. Calibration measures, on albums that are
already correct, how often the matcher picks the right release, so the auto-apply threshold can be set for 99% or better precision.
A wrong auto-tag is worse than a review, so precision matters more than how many albums apply automatically.

## What you do (about 10 minutes, once)
1. **Copy the `tools` folder** (it holds `Export-LibraryTags.ps1` and `TagLibSharp.dll`) to the PC that has the music library.
2. **Run the export** in PowerShell on that PC, once for each library folder you want included. Use the folder path exactly as Emby has it:
   ```
   powershell -ExecutionPolicy Bypass -File Export-LibraryTags.ps1 -Library "<library folder>" -MaxAlbums 250 -Out library-tags-main.json
   ```
   Suggested: main folder 250 albums, and 100 each for the kids and shared folders (a mix of styles tests the matcher better). Use a different `-Out` name each time. It only reads tags, takes a few minutes, and changes nothing.
3. **Copy the JSON files** to `C:\dev\label-maker-emby\docs\local\` on the development PC (that folder is git-ignored, so the files are never pushed).
4. **Tell me when they are there.**

## What the export contains
Artist, album, title, track and disc numbers, year, length and MusicBrainz release id per track, plus the folder path under the library
root. No audio, no absolute paths, no server or share names, no account names. The script prints how many albums carry a MusicBrainz release
id: those are the ones with a trustworthy answer. If fewer than half do, say so and we will pick another way to get answers.

## What happens next (my side)
- A calibration harness runs the matcher over the exported albums, caches every MusicBrainz answer on disk (so reruns are free and polite),
  and reports precision and the share applied automatically at each threshold.
- Each album is tested the way real downloads arrive: a random subset of 1 to 6 tracks (partial albums), with the tags left as they are, with
  the album name removed, with "Track 01" style titles, and with noise such as "[FLAC]" in the album name.
- An edition counts as correct when it has the same recordings in the same places as the album's own release id (editions of one album are
  interchangeable), or is the release itself.
- Every miss is listed with the reason, so we can fix the matcher or send that kind of album to review.

First run: the 92-track test library on the development PC, before your export arrives.

## Single tracks (2026-10-14, after the owner's MusicGrabber sample)
A MusicGrabber single arrives as an album folder with one track, no `.albuminfo`. New scenarios in the harness (`single track, tags as they are` and `... with [FLAC] noise`) take one repeatably-chosen track per library album. New `--noid-out <file>` writes the yes/no list for albums with no release id (a local file: it names real folders).
- **Result at the 0.92 bar, tags as they are, 287 albums:** 247 of 287 (86%) applied automatically; 8 counted wrong. Of those 8: 1 is an artifact (the library's release no longer exists on MusicBrainz), 5 are the same album as another edition (not a real error), and 2 are real: two same-named albums (a soundtrack against another recording) where the library's album was not even among the candidates, and a compilation. So real precision for singles is about 99.2%, near the 99% target.
- **New rule from this** (`AlbumMatcher.Finish`): a different MusicBrainz album with the very same title that fits equally well goes to review instead of being picked (`SameNameAlbumTests`). Cost: about 5 more albums in review (full albums and singles each), prevents about 1 wrong match. A wider rule (any other album, including "Deluxe") cost 13 extra reviews for 2 fewer wrong, and was rejected.
- **Full-album numbers moved slightly** (0.92 bar, tags as they are): 232 applied (81%), 10 counted wrong (98.3% by same album).
- Remaining weakness: the search looks at only 5 release groups; a very common album title can push the library's own album out of the candidates (the "Sound of Music" case). A later improvement: search again with the track title when the album is not found among the candidates.
