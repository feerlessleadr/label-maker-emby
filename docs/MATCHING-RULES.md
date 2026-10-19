# How Label Maker decides, in plain English

This page explains every rule Label Maker uses when it looks at an album that arrived in an inbox: what the rule means, why it exists, what it
does to your files, and which setting changes it. The same wording is meant for the plugin's settings page. The numbers quoted come from a test on
287 albums from the owner's real library (see section 8).

## 1. What Label Maker is deciding
A download folder arrives (usually a few tracks of an album, because MusicGrabber fetches track by track). Label Maker has to answer one
question: **which release on MusicBrainz is this?** A "release" is one specific published version of an album, with a fixed list of songs.
Once it knows, it can write the right tags, name the files the way Picard does, and file the album in the right library folder.

It would rather ask you than guess. So every album ends in exactly one of three places:

| Outcome | What it means | What happens to your files |
|---|---|---|
| **Applied automatically** | Label Maker is sure. | Tagged, renamed, moved to the library. Nothing to do. You can undo it. |
| **Needs your review** | A likely match exists, but something was not certain enough. | Nothing is touched. The album waits in the inbox, with the reason in a plain sentence and the best candidates to choose from. |
| **No match** | Nothing on MusicBrainz fits well enough. | Nothing is touched. The album stays in the inbox and appears under "Needs review, No match". |

While **dry run** is on (it is on by default) nothing is ever changed: Label Maker only shows what it would have done.

## 2. Where the candidates come from
1. **MusicGrabber's hint.** If the folder has a hidden `.albuminfo` file, it names the exact release MusicGrabber chose. Label Maker looks that one release up and
   checks your files really fit it. If they do, that is the answer (one lookup, no searching). If they do not, the hint is ignored and it searches.
2. **Ids already in the tags.** Files that were tagged by Picard (or a similar tool) already carry a MusicBrainz release id. Label Maker looks that release up and checks your files really fit it, exactly as for MusicGrabber's hint. If they fit, that is the answer, with no searching. If they do not (the id is stale, or belongs to another album), the id is ignored and it searches. If the files in one folder carry **different** release ids, Label Maker never applies the album automatically and asks you which album they belong to.
3. **A search, with fallbacks.** Label Maker looks for the album on MusicBrainz by name and artist. The most exact search ("this album name AND this artist") is tried first, but MusicBrainz
   often finds nothing with it (artists are stored under many spellings), so if nothing plausible comes back it tries looser searches, in this order: the album name alone (it then checks the artist
   itself), the album and artist words with punctuation removed, and finally every album by that artist. Each search is also tried with other spellings of the album name: the name as written
   (after removing noise such as "[FLAC]", "(320kbps)", "(Deluxe Edition)", "(sfw)" or a web address), then without any trailing "(...)" such as "(Green)" or "(Soundtrack from the Film)", then
   with "2" written as "II" or the other way round. All kinds of dashes and quotes are treated as plain ones. Your own tags are never changed by any of this. It stops as soon as some edition
   fits your files well enough to suggest, and gives up after 8 searches.
   An album from the search is kept when its name is close to yours (also when it is yours plus a subtitle, such as "The Little Mermaid" and "The Little Mermaid: Original Motion Picture
   Soundtrack") and its artist plausibly matches (similar names, a shared name such as "Alan Menken" in "Alan Menken & Howard Ashman", or "Various Artists"). If no album passes both tests,
   Label Maker also considers an album with the right name under another artist name ("mgk" is "Machine Gun Kelly" on MusicBrainz) and an album by the same artist under a similar name
   (renamed albums such as "Greatest Hits - Chapter One", listed on MusicBrainz as "The Hits - Chapter One"). Those are always sent to review, never applied automatically (rule 4 below).
   It checks **every official edition** of up to **5 different albums with that name** (a band can have a single, two albums and a live record all called "High Voltage").4. Only **official** releases are considered. Bootlegs, promotional copies and withdrawn releases are skipped, because they show up at the top of searches and are usually wrong.

## 3. How a file is matched to a track
Partial albums are normal: you might have 3 of 14 songs. So missing tracks cost nothing. What matters is that **every file you have lands on its own,
different track** of the release. Each file is compared with each track using, whenever both sides know it:
- the **song title** (ignoring capitals, accents, punctuation, curly versus straight apostrophes, and "&" versus "and"; a trailing "(Remastered)" or "[Live]" lowers the score a little but does not break the match) — 60% of the file's score;
- the **track number** — 20%;
- the **disc number** — 10% (this is what tells the same song on disc 1 and disc 2 apart);
- the **length**, within 3 seconds counting fully, fading to nothing at 15 seconds — 10%.

Label Maker then finds the pairing that gives the best total, so two files can never claim the same track.

## 4. The score
Each edition gets a score from 0 to 1: **65% how well the files fit its tracks, 20% how close the album name is, 15% how close the artist name is.** In words:
- **1.00** every title, number and name fits exactly.
- **0.92 and up** very close to certain (the default bar for acting without asking).
- **0.60 to 0.92** a likely match, but worth a look.
- **under 0.60** nothing fits; reported as "no match".

The year is never used to reject a release: MusicGrabber often writes a reissue's year.

## 5. The rules for acting without asking
An album is applied automatically only when **all** of these hold. If any one fails, it goes to review and the sentence says which.

1. **The score is high enough.** The best edition scores at least the *automatic threshold* (default **0.92**).
   *If it fails:* "The best match is only probable."
2. **Every file fits.** Each file must fit its track at least **0.5**. One odd file (a different song, a bonus track the release does not list) is enough to stop the whole album, so one wrong file cannot hide inside a good average.
   *If it fails:* "1 of 4 files could not be matched to a track of the best release."
3. **No close rival.** If another edition fits almost as well (within **0.05**) and is a *real* alternative, Label Maker will not guess.
   *If it fails:* "Several editions fit these files about equally."
4. **The album is listed under the same name and artist as your tags.** If it was found only because of a looser search (another artist name, a renamed album), a person confirms it. The same goes for a folder whose files carry different MusicBrainz release ids.
   *If it fails:* "MusicBrainz lists this album under a different name or artist than your tags say, so please check it." or "The files in this folder carry different MusicBrainz release ids, so please check which album they belong to."
5. **The lane allows it.** Each lane (inbox to library folder) can be set to "always ask me".
6. **Dry run is off.**
7. **The destination is free.** If a file with that name is already in the library, nothing is written ("is already in the library"), and nothing is ever overwritten.

## 6. The edition rule (the setting that most changes how often you are asked)
An **edition** is one release of an album: the US pressing, the UK pressing, a remaster, a deluxe version. They usually list the same songs, so for *your files* they
often look identical. The edition rule answers: **when two or more editions fit your files equally well, is that a tie you have to settle, or can Label Maker just pick one?**

**Same titles (the default).** Editions count as interchangeable when they list **the same song titles in the same places for the files you have.** Label Maker then
picks one for you: first the edition from your preferred countries (default: United States, then worldwide, then Great Britain), and if that does not decide it, the
earliest release date.
- *What it means for you:* the folder name, file names and every tag you can see come out **the same whichever edition it picks.**
- *What can differ:* the hidden MusicBrainz ids written into the tags (the release id and the recording ids). They may belong to a different edition of the same album, for example the remaster instead of the original. Emby treats them as the same album; the ids only matter to tools that read MusicBrainz ids.
- *What it never does:* treat editions that list **different titles or a different order** for your files as the same. Those are real differences, and the one that fits better wins only if it is clearly ahead (rule 3 above); otherwise you are asked.

**Same recordings (strict).** Editions count as interchangeable only when MusicBrainz lists **the very same recording** of every one of your songs. A remaster has its own
recordings, so whenever both an original and a remaster fit your files, you are asked which one.
- *What it means for you:* the hidden ids are always exactly right, but more albums wait in your review list.

**What testing on your library showed:** with the strict rule about 72% of albums were applied without asking; with the titles rule about 83%. Both applied only
albums that landed on the right album with the right song titles in the right places. The titles rule is the default because the extra 11% would otherwise all be
questions with no visible difference between the answers.

**Two different albums with the same name (added 2026-10-14).** Several editions of one album are treated as interchangeable when they read the same for your files. But when two *different* MusicBrainz albums that share the very same title both fit your files perfectly (for example a movie soundtrack and a stage cast recording, both called "The Sound of Music"), Label Maker does not pick one: it asks you. A differently named album, such as a "Deluxe Edition", does not trigger this. The rule matters most for single tracks, where one file cannot tell two same-named albums apart. Measured on your library it moved about 5 of 287 albums from automatic to review and prevented about 1 wrong automatic match.

## 7. When Label Maker cannot decide (and what you can do)
Status: the matching, these sentences and the review actions (see "What you can do about it" below) are built in the engine. The review screen that shows them, the automatic retries and the "keep both" choices are designed but **not built yet** (they come with the plugin shell and the review screen); until then an album that is not applied simply stays in the inbox untouched.

The plain sentences you will see, and what each means:

| What you see | What it means | What to do |
|---|---|---|
| "MusicBrainz lists this album under a different name or artist than your tags say, so please check it." | The album was found only by a looser search: another artist name ("mgk" for "Machine Gun Kelly") or a renamed album. | Check that it is the right album, then approve it or pick another. |
| "The files in this folder carry different MusicBrainz release ids, so please check which album they belong to." | Some files say they belong to one release, others to another (a folder mixing editions or albums). | Pick the right edition in the review screen, or split the folder. |
| "Several editions fit these files about equally." | Two or more editions fit and they list different titles or order for your files. | Pick the edition in the review screen. |
| "Two different albums on MusicBrainz fit these files equally, so please pick the right one." | Two separate albums with the very same title list your track identically (a film soundtrack and a cast recording both called "The Sound of Music"). A single track cannot tell them apart. | Pick the right album in the review screen. |
| "1 of 4 files could not be matched to a track of the best release." | One file does not belong to the album Label Maker found (or the release does not list it). | Look at that file: wrong album, a bonus track, or a mislabeled file. Fix the tag or keep the album as is. |
| "The best match is only probable." | The best edition scored under the automatic threshold. | Check the suggestion and approve it, pick another, or search by hand. |
| "Nothing on MusicBrainz fits these files well enough." | The best candidate scored under 0.60. | Search by hand, paste a MusicBrainz link, or keep your tags. |
| "No release was found for this album." | The search found nothing usable. | Same as above. |
| "This folder holds 2 different albums by their tags. Use \"Split by album\" on the review screen to put each album in its own folder, or sort them yourself." | The tags name more than one album (for example "Insomniac" and "Dookie"). | Choose "Split by album" (below), or move each album into its own folder yourself; the inbox is checked again. |
| "1 file(s) could not be read (name). Fix or remove them and the album is checked again." | A file is damaged or is not music. | Replace or delete that file. |
| "These files have no album name to search for." | The tags have no album. | Type the album name in the review screen's search. |
| "... is already in the library." | A file with the same name is already there. | Keep both (suffix), replace only if you choose, or discard the new one. |
| "The path ... is N characters long." | The new file path would be too long for Windows. | Shorten a name or accept a shorter naming pattern. |
| "MusicBrainz could not be reached" / "stayed busy" | Not a match problem: the service is down or busy. | Nothing. The album is retried automatically and never counted as "no match". |

### What you can do about it (the review actions; the screen that shows them comes with the plugin)
Every action below is checked first and refused, with a plain sentence, if it would be unsafe. An album that is being checked or filed at that moment cannot be touched, and an album that is already filed has to be undone first.

- **Look before you choose ("preview").** Shows which MusicBrainz edition you are about to use, how well each of your files fits it, where each file would go in your library and anything that would stop it. Nothing changes.
- **Choose another edition ("apply").** Tags your files from the edition you picked and files them. Your tap means yes, so this really happens even when Label Maker as a whole is still in "dry run" mode (dry run only governs what Label Maker does by itself). The same safety checks as an automatic apply run first: every file needs a track, no two files may get the same name, nothing may land outside the library, and paths must not be too long.
- **Search by hand.** Type an artist and an album; Label Maker searches MusicBrainz and shows each edition with a score for how well your files fit it.
- **Paste a MusicBrainz link or id.** A link to a release page (one edition), a release group page (the album as a whole: its editions are listed) or just the long id. Anything else is refused with a sentence telling you what to paste.
- **Say which track a file is ("mapping").** If one file does not fit (a bonus track, a mislabeled file), you can tell Label Maker "this file is disc 1, track 7". Files you place yourself are never counted as a poor fit; the other files are matched to the tracks that are left. A mapping that names a track that does not exist, or gives two files the same track, is refused.
- **Keep my tags and file it.** For music MusicBrainz does not have. Label Maker does not change a single tag and does not look anything up; it only moves and renames the files using the tags you already have (the same naming pattern as everything else). Every file needs a title, an album and an artist, and no two files may share a track number on a disc. A MusicBrainz id already in the tags is kept.
- **Skip for now.** The album stays in the inbox untouched. Even if its files change, Label Maker leaves it alone until you choose "check again". The same hold applies after an undo, so an album you undid is never redone behind your back.
- **Check again.** Puts the album back in line for a fresh look on the next pass (use it after fixing files by hand).
- **Discard.** Moves the album's files into a `_Trash` folder inside the inbox (in a dated folder of its own with a list of where every file came from). **Nothing is ever deleted by Label Maker.** Label Maker ignores folders that start with an underscore, so a discarded album is not picked up again. Emptied inbox folders are tidied away.
- **Restore.** Moves a discarded album back to exactly where it was, then waits for your decision. If a file with the same name has since appeared in that place, nothing is restored and you are told why.
- **Undo.** For an album that was filed: puts every file back in the inbox with its old tags and names (and the cover and lyric files that moved with it), and leaves the album waiting for your decision.
- **Split by album.** For a folder whose tags name several albums (the sentence above). Label Maker puts each album in a folder of its own inside the mixed folder, named after the album, and each music file takes its lyrics file with it. Nothing is renamed or retagged, "CD1"/"CD2" folders inside are kept, and a cover or other loose file stays where it was. Each new folder is then checked as an album of its own on the next pass. Albums are told apart by the album name in the tags, so if any file has no album name Label Maker refuses and tells you which ones; it never overwrites a folder that is already there (it numbers the new one instead).

## 7b. Situations Label Maker has not been tested on yet
Some kinds of albums were not part of the test: files with no MusicBrainz id to check against, MusicGrabber's own "Singles" folders, classical music, several albums mixed in one folder, and files whose tags disagree with each other. The rule for all of them is the same: **until it has been measured on your real downloads, Label Maker asks you instead of acting.** (Update 2026-10-14: MusicGrabber, as the owner runs it, does not use a "Singles" folder for finished downloads; a single track arrives as an album folder with one track, which is tested.)
- It starts in **dry run**, so nothing moves while you watch what it would have done.
- When you switch automatic applying on, you do it **one lane at a time** (suggested: start with the main music lane, leave the kids and shared lanes on "always ask me" until their numbers look good).
- Every applied album can be **undone**, with its old tags restored. An undo is recorded as a miss, so the settings can be tuned from real mistakes.
- A folder that looks like classical music, an audiobook or a podcast, or that holds several different albums, is never applied automatically.

## 8. What your library's test says
On 287 albums from the owner's library (the real albums average about 3 tracks, so these were partial albums). 271 of them have tags that agree with their own MusicBrainz id, which makes them a trustworthy answer key; the other 16 have edited tags or an id that no longer exists, so they cannot say what the right answer is.
- **Default settings (same-titles edition rule, automatic bar 0.92):** 85% of the trustworthy albums were applied automatically, and **every one of them landed on the right album with the right song titles in the right places (232 of 232)**. With "[FLAC]" added to every album name it was 82% and again 232 of 232 right (219 of 219).
- **Strict edition rule:** 73% applied automatically, all exactly right (199 of 199, even down to the recording ids).
- **Nothing found:** 1 album of 287 (0.3%), a brand-new soundtrack whose name clashes with many other albums. Before the search fallbacks it was 14 (5%).
- The rest, about 14% of albums, went to review with a reason; in about 4 of 5 of those the best candidate shown was already the right one.
- **A release id hint** (from MusicGrabber's `.albuminfo` or already in the tags): with the right id, 280 of 287 albums were decided with a single lookup and no search, 99% of the trustworthy albums were applied automatically, and none was wrong. With a **wrong** id (the id of a different album) the hint was ignored every time and the result was exactly what no hint gives (85%, none wrong): a bad hint cannot cause a wrong automatic apply.
- Not yet measured: albums with no MusicBrainz id in their tags (mostly the kids library), and MusicGrabber's own hint on a real inbox (the test used the library's own ids in its place).
## 8b. When an album is picked up, and its life in the queue
Status: this is built and tested in the engine. The Emby plugin will run it on a timer; the review screen and the phone alerts come later.

**When Label Maker looks.** On every pass (about once a minute; the plugin sets the interval) it looks at every lane's inbox again. It does not rely on Windows file-change notifications, because they are unreliable over network shares and with Docker, so looking again is the real trigger. A "Check inbox now" button will just start a pass early.

**What counts as an album.** A folder with music files in it is one album. The files in "CD1", "Disc 2" and similar folders count with the album folder above them. Every track under a folder called "Singles" (MusicGrabber's layout) is handled on its own. Folders whose names start with "." or "_" (MusicGrabber's `.trash`, Label Maker's `_Trash`), "Playlists" folders and the recycle bin are never looked into, and playlist files are left where they are. **A single song downloaded with MusicGrabber is just an album folder with one track in it** (for example Albums/Leonard Cohen/Various Positions/Hallelujah.flac with its lyrics file and no .albuminfo): it is matched like any partial album, using the album and artist in the tags, and filed into the album folder in the library. The "Singles" rule above is kept in case another MusicGrabber setting uses it.

**When a download is finished.** All of these must hold, otherwise Label Maker waits:
- nothing in the folder has changed for **60 seconds** (names, sizes and times of every file, so a new cover or lyrics file counts too);
- there is no temporary download file in it (`.part`, `.crdownload`, a sync program's temporary file, a name starting with "~");
- no music file is still open in another program (still being written, or being scanned for viruses).
Any change starts the 60 seconds again. After a restart of Emby every album also waits its 60 seconds again.

**Order and pace.** Oldest first, one album at a time, at most 20 per pass, so a drop of hundreds of albums works through over time without holding anything else up. MusicBrainz is asked politely, about one request a second.

**The states an album goes through.**

| State | Meaning | What happens next |
|---|---|---|
| Waiting | Seen, still downloading, or waiting its turn, or waiting to try again | Handled when it is finished and due |
| Needs review | A likely match, but something was not certain enough | Waits for you; nothing is touched |
| No match | Nothing on MusicBrainz fits | Stays in the inbox for you |
| Ready | Dry run: it would have been applied | Nothing is touched; switch dry run off and the next check (or **Check inbox now**) files it by itself, by the same rules as any new album |
| Done | Tagged and moved into the library | Finished (it can be undone) |
| Duplicate | A file with that name is already in the library | Nothing is written; you choose |
| Failed | Something went wrong (a damaged file, the library folder not reachable) | Fix the cause; it is checked again when the folder changes |
| Skipped | The album was taken out of the inbox by hand | Finished |

**When MusicBrainz is busy or cannot be reached.** The album goes back to Waiting and is tried again after 1 minute, then 5, then 15, then every 60 minutes. It is never reported as "no match" because of that.

**When files arrive later.** If a new track lands in a folder that was waiting for review, had no match or had failed, the album starts over (it is looked at again from the beginning, as the extra track may change the answer).

**If the server stops in the middle.** An album that was being matched starts over. An album that was being written is finished from its journal, without matching again.

**If the inbox cannot be reached** (a network share drops): nothing is marked gone and nothing is lost; the albums are picked up again when it comes back.

**Not built yet.** Splitting a folder that holds several different albums (it goes to review with that reason instead) and spotting classical music, audiobooks and podcasts.

## 8c. Lane rules, the health check and the memory of MusicBrainz answers
Status: built in the engine (2026-10-14); the settings page that shows them comes with the plugin. (Code: `Inbox/LaneValidator.cs`, `Pipeline/Preflight.cs`, `MusicBrainz/ResponseCache.cs`.)

**Lane rules** (checked every time the lane list is saved; each problem is explained in a sentence and tied to the field to fix):
- Every lane needs its own id (letters, digits, dashes, underscores; it never changes once albums are queued), a name, an inbox folder and a library folder, each a full path: a drive letter such as `D:\` or a network path such as `\\nas\music`.
- Two lanes may not share an inbox, or albums would be filed in the wrong library.
- An inbox may not be inside another lane's inbox: both lanes would see the same albums.
- An inbox and a library folder may not be the same folder, or inside each other. Music already in the library would be picked up again as a new download. This is checked for a lane's own library and for every other lane's.
- Folder names are compared without regard to capital letters, slash direction or a trailing slash, so `D:\Music Inbox\` and `d:/music inbox` count as the same folder.
- A lane cannot be removed while one of its albums is being checked or filed. Removing a lane never touches files.

**The health check (preflight)** tries what Label Maker will really do, as the account Emby runs as, with small throw-away test files (their names start with a dot and they are deleted again):
- *Inbox found* and *Library found*: the folders can be reached. If not, the message says to check the folder or share and which account needs to be able to open it.
- *Inbox changes*: Label Maker can create, rename and delete a file in the inbox. It needs this because it moves albums out of the inbox. If not: "Give the account Emby runs as (NAME) Modify permission on that folder."
- *Library changes*: Label Maker can create a folder and a file in the library, and rename and delete the file. If not, the same fix is named for the share.
- *Free space* on the library disk: a warning under 1 GB, an error under 100 MB, and "could not be read" (no problem) when the system will not say. The exact space needed is checked again before each album.
- *Path length*: a warning when the library folder path is so long that albums with long names might not fit under the 240-character limit.
- *Same disk*: good to know, never a problem. Different disks mean each file is copied, checked and then deleted from the inbox, so the first big albums take longer.
A lane with any error is left alone until the problem is fixed, and the settings page shows it in red.

**Memory of MusicBrainz answers.** Label Maker keeps every answer it gets from MusicBrainz on disk, so the same question is never asked twice (it also keeps you well inside MusicBrainz's one-request-a-second rule, and lets an album be checked again offline). How long an answer is trusted depends on the question: an album edition's own data 30 days, the list of editions of an album 3 days, a search 1 day, and "MusicBrainz has no such thing" 1 day. Busy or failed answers are never kept, an unreadable file simply counts as not remembered, and the oldest answers are dropped first when there are more than 20,000.

## 9. Glossary
- **Release**: one specific published version of an album, with a fixed song list. Has a MusicBrainz id.
- **Edition**: informal word for a release when comparing versions of the same album (US, UK, remaster, deluxe).
- **Release group**: MusicBrainz's name for "the album" as a whole, covering all its editions.
- **Recording**: one specific recorded performance of a song. A remaster is a different recording of the same song.
- **MusicBrainz id (MBID)**: the long code that identifies a release or recording. Written into tags so other tools recognize the music.
- **Lane**: one inbox folder paired with one library folder.
- **Dry run**: show what would happen, change nothing.

## 8d. Cover art (built 2026-10-19, plugin 0.1.6.0 and later)
Code: `CoverArt/CoverArtClient.cs`, `CoverArt/CoverArtFiller.cs`. Setting: **Settings > Naming and tags > Download cover art for albums that have none** (on by default; the page says what is sent).
- **When.** Only right after an album has been filed (automatically, or by your choice on the Albums tab) and only when its library folder holds no `cover`, `folder` or `albumart` image (`.jpg`, `.jpeg`, `.png`). A cover that came with the download, or that the library already had, is never replaced, and nothing is asked of the Cover Art Archive in that case.
- **What.** The front cover of the edition that was used, 500 pixels wide (the same size as the owner's Picard setting), saved as `cover.jpg` (or `cover.png`) next to the music, where Emby looks for it. The image must really be a JPEG or a PNG (judged by its first bytes) and at most 8 MB.
- **What is not done.** The picture is not written into the files' tags (Picard's "embed cover art" is a different thing; the library keeps its files as they are). If the release has no front cover on the archive, or the archive cannot be reached, the album is filed all the same and no cover is added; Label Maker does not retry.
- **Sent.** Only the album's MusicBrainz release id, to `coverartarchive.org`. No file names, no folder names.
- **Undo.** Undoing an album also removes the cover Label Maker added for it (never one that was there before).
- **The Albums tab** says "Cover art added." at the end of the album's sentence when it happened.
