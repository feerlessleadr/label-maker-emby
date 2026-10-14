# Label Maker (Emby plugin): instructions for Claude

**Start with `docs/HANDOFF.md`** (state, owner environment, decisions, working rules, next steps), then `docs/PHASE0.md` (spike results) and `docs/DESIGN.md` (the full design and plan).

- Status (2026-10-14): design and Phase 0 done; engine in `src/LabelMaker.Core` has naming, tags, MusicBrainz client, matcher, planner and applier (see HANDOFF sections 11 and 12). No inbox scanner, plugin shell or UI yet.
- Template to copy: `C:\dev\hall-pass-emby` (see its `docs/HANDOFF.md` and `CLAUDE.md`). The phone app is `C:\dev\embidextrous` (its `docs/notifications-plan.md` holds the notification-rules requirement).
- Commits are authored as `feerlessleadr <github@thecgkg.com>` (already set in this clone). Never the work email.
- Release only when the owner says "publish it" (notes committed first, CI and CodeQL green on the exact commit, tag = AssemblyVersion).
- Never put the owner's LAN IP, share names, Windows username or Picard.ini in tracked files: they live in git-ignored `docs/local/` (ENVIRONMENT.md, Picard.ini); tracked docs use placeholders such as `<server>`.
- Plugin facts that bite: embed TagLibSharp and resolve it with `AssemblyLoadContext.Default.Resolving`; plugins have no `Assembly.Location`; admin pushes reach administrator user sessions only; Emby shows new folders 1.5 to 2 minutes after `ReportFileSystemChangeComplete`.
