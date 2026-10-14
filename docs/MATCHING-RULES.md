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
2. **A search, with fallbacks.** Label Maker looks for the album on MusicBrainz by name and artist. The most exact search ("this album name AND this artist") is tried first, but MusicBrainz
   often finds nothing with it (artists are stored under many spellings), so if nothing plausible comes back it tries looser searches, in this order: the album name alone (it then checks the artist
   itself), the album and artist words with punctuation removed, and finally every album by that artist. Each search is also tried with other spellings of the album name: the name as written
   (after removing noise such as "[FLAC]", "(320kbps)", "(Deluxe Edition)", "(sfw)" or a web address), then without any trailing "(...)" such as "(Green)" or "(Soundtrack from the Film)", then
   with "2" written as "II" or the other way round. All kinds of dashes and quotes are treated as plain ones. Your own tags are never changed by any of this. It stops as soon as some edition
   fits your files well enough to suggest, and gives up after 8 searches.
   An album from the search is kept when its name is close to yours (also when it is yours plus a subtitle, such as "The Little Mermaid" and "The Little Mermaid: Original Motion Picture
   Soundtrack") and its artist plausibly matches (similar names, a shared name such as "Alan Menken" in "Alan Menken & Howard Ashman", or "Various Artists"). If no album passes both tests,
   Label Maker also considers an album with the right name under another artist name ("mgk" is "Machine Gun Kelly" on MusicBrainz) and an album by the same artist under a similar name
   (renamed albums such as "Greatest Hits - Chapter One", listed on MusicBrainz as "The Hits - Chapter One"). Those are always sent to review, never applied automatically (rule 4 below).
   It checks **every official edition** of up to **5 different albums with that name** (a band can have a single, two albums and a live record all called "High Voltage").3. Only **official** releases are considered. Bootlegs, promotional copies and withdrawn releases are skipped, because they show up at the top of searches and are usually wrong.

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
4. **The album is listed under the same name and artist as your tags.** If it was found only because of a looser search (another artist name, a renamed album), a person confirms it.
   *If it fails:* "MusicBrainz lists this album under a different name or artist than your tags say, so please check it."
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

## 7. When Label Maker cannot decide (and what you can do)
Status: the matching and these sentences are built. The review screen, the automatic retries and the "keep both" choices below are designed but **not built yet** (they come with the plugin shell and the review screen); until then an album that is not applied simply stays in the inbox untouched.

The plain sentences you will see, and what each means:

| What you see | What it means | What to do |
|---|---|---|
| "MusicBrainz lists this album under a different name or artist than your tags say, so please check it." | The album was found only by a looser search: another artist name ("mgk" for "Machine Gun Kelly") or a renamed album. | Check that it is the right album, then approve it or pick another. |
| "Several editions fit these files about equally." | Two or more editions fit and they list different titles or order for your files. | Pick the edition in the review screen. |
| "1 of 4 files could not be matched to a track of the best release." | One file does not belong to the album Label Maker found (or the release does not list it). | Look at that file: wrong album, a bonus track, or a mislabeled file. Fix the tag or keep the album as is. |
| "The best match is only probable." | The best edition scored under the automatic threshold. | Check the suggestion and approve it, pick another, or search by hand. |
| "Nothing on MusicBrainz fits these files well enough." | The best candidate scored under 0.60. | Search by hand, paste a MusicBrainz link, or keep your tags. |
| "No release was found for this album." | The search found nothing usable. | Same as above. |
| "These files have no album name to search for." | The tags have no album. | Type the album name in the review screen's search. |
| "... is already in the library." | A file with the same name is already there. | Keep both (suffix), replace only if you choose, or discard the new one. |
| "The path ... is N characters long." | The new file path would be too long for Windows. | Shorten a name or accept a shorter naming pattern. |
| "MusicBrainz could not be reached" / "stayed busy" | Not a match problem: the service is down or busy. | Nothing. The album is retried automatically and never counted as "no match". |

In the review screen (not built yet) you will be able to: choose another edition with one tap, search by album and artist, **paste a MusicBrainz link or id**, **approve** the suggestion, **keep your own tags and file it** (moved and renamed from the tags you already have, with no MusicBrainz lookup), **skip for now**, or send it to the trash folder.

## 7b. Situations Label Maker has not been tested on yet
Some kinds of albums were not part of the test: files with no MusicBrainz id to check against, MusicGrabber's own "Singles" folders, classical music, several albums mixed in one folder, and files whose tags disagree with each other. The rule for all of them is the same: **until it has been measured on your real downloads, Label Maker asks you instead of acting.**
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
- Not yet measured: albums with no MusicBrainz id in their tags (mostly the kids library), and MusicGrabber's own release id hint on a real inbox.
## 9. Glossary
- **Release**: one specific published version of an album, with a fixed song list. Has a MusicBrainz id.
- **Edition**: informal word for a release when comparing versions of the same album (US, UK, remaster, deluxe).
- **Release group**: MusicBrainz's name for "the album" as a whole, covering all its editions.
- **Recording**: one specific recorded performance of a song. A remaster is a different recording of the same song.
- **MusicBrainz id (MBID)**: the long code that identifies a release or recording. Written into tags so other tools recognize the music.
- **Lane**: one inbox folder paired with one library folder.
- **Dry run**: show what would happen, change nothing.
