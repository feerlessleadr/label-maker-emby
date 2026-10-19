# Security and privacy

## What leaves your server
- To **musicbrainz.org** (the public MusicBrainz web service): the **artist, album and track titles**, track counts and lengths of the albums being matched, plus the MusicBrainz ids already in your files. One request a second at most, with a User-Agent that names this project (and your contact address if you enter one in the settings, so MusicBrainz can reach you).
- To **coverartarchive.org** (the Cover Art Archive, run by the same project), **only when an album has just been filed and its library folder has no cover image**: the album's MusicBrainz release id, to ask for its front cover (500 pixels wide). The switch is **Settings > Naming and tags > Download cover art for albums that have none**. Turn it off and nothing is sent there.
- Nothing else. No audio, no file names, no folder or share names, no account or server details, no usage statistics. There is no other outside service.
- **Phone alerts** go over Emby's own connection to administrator sessions only (Emby's websocket). They name albums by artist and album, never by folder path or share name.

## What the plugin can do to your files
- It **moves, renames and tags** music files inside the lanes you configure, only after a match is certain (or you approve it) and only when dry run is off for automatic work. Every filed album has a record that **Undo** uses to put files and tags back.
- It **never deletes** music. "Discard" moves an album to a `_Trash` folder inside its inbox.
- A destination is always built from the library folder plus a sanitized relative path and is checked to stay inside the library folder. Text from tags and MusicBrainz is never used as a path without that.
- Files are changed one album at a time; a move across disks copies, verifies size and SHA-256, and only then removes the original.

## Who can use it
Every plugin route needs an **Emby administrator** token. The plugin has no key or account of its own. Settings (including lane folders) are written through Emby's own plugin settings route.

## Reporting a problem
Please report a security issue privately with a GitHub security advisory on this repository (Security tab, "Report a vulnerability") rather than a public issue.
