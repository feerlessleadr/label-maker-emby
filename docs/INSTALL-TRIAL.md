# Installing the Label Maker trial build

This is a **trial build** (version 0.1.3.0; if you installed 0.1.1.0, update: its page did not open), not a release. It starts in **dry run**: it matches albums and shows what it would do, and moves or changes nothing by itself. Your own choices on the Albums tab are still carried out.

## 1. Install
1. Close Emby, or leave it running and restart it at step 3.
2. Copy `LabelMaker.dll` into the `plugins` folder of Emby's data folder. On a Windows install that is usually `%AppData%\Emby-Server\programdata\plugins` (the folder that already holds `HallPass.dll` and `HallMonitor.dll`). The file's SHA-256 is in the notes next to it.
3. Restart Emby. After about a minute **Label Maker** appears in the dashboard menu under **Advanced**.
4. It checks the inboxes about 20 seconds after Emby starts, then every 60 seconds.

Label Maker keeps its files in `<Emby data>\plugins\configurations\LabelMaker.Data` (its list of albums, the undo records, the alert log and a cache of MusicBrainz answers) and its settings in `...\configurations\LabelMaker.xml`.

## 2. Set up one lane first
Open **Label Maker > Settings > Lanes > Add a lane**:
- **Inbox folder:** where MusicGrabber puts finished downloads, for example `E:\Downloads\Music`. This is the folder that holds `Albums`.
- **Library folder:** the folder Emby's music library reads, for example `\\<server>\<main-share>`. A network path is the safer choice when Emby runs as a Windows service, because mapped drive letters belong to one logon session.
- Save, then open **Dashboard > Check the folders**. Every line should be OK. If one says the library cannot be written, give the account Emby runs as **Modify** permission on that share and folder (the message names the account).

Start with the main music lane only. Add the kids and shared lanes when the first one behaves.

## 3. The trial, step by step
1. **Leave dry run on.** Let a few downloads arrive. After the files stay unchanged for a minute, each album appears on **Albums** as *Ready (dry run)*, *Needs review* or *No match*, with a plain sentence saying why.
2. **Look at Ready albums.** Open one, press *Use the best match*, and read the preview: where each file would go and what it would be named. Nothing changes until you press *File the album with this edition*.
3. **Work the Needs review list** with the buttons on the album: pick another edition, search by artist and album, paste a MusicBrainz link, say which track a file is, *Keep my tags and file it*, *Skip*, *Split by album* (a folder holding several albums), or *Discard* (to the inbox's `_Trash` folder; nothing is deleted).
4. **Undo.** Any album you or Label Maker filed can be undone on the Albums tab (show *Filed*): files return to the inbox with their old tags and names.
5. **When the Ready list looks right** for a few days, turn dry run off (Dashboard, or Settings > Checking). From then on albums that match with certainty are filed by themselves. For a lane you do not trust yet, tick **Always ask me first for this lane** in Settings > Lanes.
6. Emby shows a newly filed album about 90 to 120 seconds later; that delay is Emby's, not Label Maker's.

## 4. Things to know
- **MusicBrainz** is asked for artist, album and track names only, one request a second, and answers are remembered on disk. If it stops answering, albums wait and retry by themselves; the Dashboard says so.
- **Phone alerts** are sent over Emby to the Embidextrous app (Settings > Phone alerts has the switches). The app side is not built yet, so nothing is shown on the phone for now.
- **Not built yet:** cover art download, choices for an album that is already in the library (it is shown as *Already in library*), and spotting classical music or audiobooks. Such albums show up on the list for you to decide.
- **Naming** is Picard's stock script exactly; tags are written like your Picard (ID3v2.4, UTF-8, ID3v1 kept). Both are settings.

## 5. If something goes wrong
- Press **Pause** on the Dashboard: nothing is looked at or changed while paused.
- Emby's log lines from Label Maker start with `[Label Maker]`.
- To remove it: stop Emby, delete `LabelMaker.dll`, `configurations\LabelMaker.xml` and the `configurations\LabelMaker.Data` folder, start Emby. Files already filed stay where they are.
