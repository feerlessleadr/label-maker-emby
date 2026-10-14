define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-select', 'emby-checkbox', 'emby-textarea'], function (BaseView, loading, toast) {
    'use strict';

    const pluginId = 'a29bf822-41ab-4e14-a814-a8d386e7f745';
    const PAGE_SIZE = 25;
    const PAGES = '#lmDashPage, #lmAlbumsPage, #lmSettingsPage';
    const STATE_LABELS = {
        Waiting: 'Waiting', Matching: 'Checking', NeedsReview: 'Needs review', NoMatch: 'No match', Ready: 'Ready (dry run)', Applying: 'Filing',
        Done: 'Filed', Failed: 'Failed', Skipped: 'Skipped', Duplicate: 'Already in library'
    };
    const TILES = [
        ['NeedsReview', 'Need review', 'lm-review'], ['NoMatch', 'No match', 'lm-review'], ['Ready', 'Ready (dry run)', 'lm-ready'],
        ['Waiting', 'Waiting', ''], ['Done', 'Filed', 'lm-done'], ['Failed', 'Failed', 'lm-failed'], ['Duplicate', 'Already in library', ''], ['Skipped', 'Skipped', '']
    ];

    function getJson(path, params) { return ApiClient.getJSON(ApiClient.getUrl(path, params)); }

    function postJson(path, data) {
        return ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl(path), data: JSON.stringify(data || {}), contentType: 'application/json', dataType: 'json' });
    }

    // Everything that comes from a file, a tag or MusicBrainz goes in with textContent, never as HTML.
    function el(tag, className, text) {
        const e = document.createElement(tag);
        if (className) { e.className = className; }
        if (text !== undefined && text !== null) { e.textContent = text; }
        return e;
    }

    function pill(state, label) { return el('span', 'lm-pill s-' + state, label || STATE_LABELS[state] || state); }

    function when(utc) { return utc ? new Date(utc).toLocaleString() : ''; }

    function plural(n, word) { return n + ' ' + word + (n === 1 ? '' : 's'); }

    function scoreWords(score, autoThreshold) {
        if (score === null || score === undefined) { return ''; }
        const words = score >= (autoThreshold || 0.92) ? 'Strong' : score >= 0.6 ? 'Probable' : 'Weak';
        return words + ' (' + score.toFixed(2) + ')';
    }

    function slug(text) {
        return (text || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').substring(0, 30);
    }

    function clear(node) { while (node.firstChild) { node.removeChild(node.firstChild); } }

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);
            this.page = 0;
            this.total = 0;
            this.status = null;
            this.config = null;
            this.laneRows = [];
            this.openAlbum = null;
            this.pollTimer = null;
            this.findToken = 0;
            const q = (s) => view.querySelector(s);

            q('.localnav').addEventListener('click', (e) => {
                e.preventDefault();
                const target = e.target.closest('.nav-button');
                if (!target) { return; }
                const id = target.getAttribute('data-target');
                view.querySelectorAll('.localnav .nav-button').forEach(b => b.classList.remove('ui-btn-active'));
                target.classList.add('ui-btn-active');
                view.querySelectorAll(PAGES).forEach(p => p.classList.toggle('hide', p.id !== id));
                if (id === 'lmAlbumsPage') { this.loadAlbums(); }
                if (id === 'lmSettingsPage') { this.loadSettings(); }
                if (id === 'lmDashPage') { this.refreshStatus(); }
            });

            q('#lmRun').addEventListener('click', () => this.runNow());
            q('#lmPause').addEventListener('click', () => this.togglePause());
            q('#lmDryToggle').addEventListener('click', () => this.toggleDryRun());
            q('#lmHealthBtn').addEventListener('click', () => this.healthCheck());
            q('#lmFApply').addEventListener('click', () => { this.page = 0; this.loadAlbums(); });
            q('#lmFState').addEventListener('change', () => { this.page = 0; this.loadAlbums(); });
            q('#lmFLane').addEventListener('change', () => { this.page = 0; this.loadAlbums(); });
            q('#lmFText').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); this.page = 0; this.loadAlbums(); } });
            q('#lmPrev').addEventListener('click', () => { if (this.page > 0) { this.page--; this.loadAlbums(); } });
            q('#lmNext').addEventListener('click', () => { if ((this.page + 1) * PAGE_SIZE < this.total) { this.page++; this.loadAlbums(); } });
            q('#lmAddLane').addEventListener('click', () => this.addLaneRow({ Id: '', Name: '', InboxPath: '', LibraryPath: '', AutoApply: true, isNew: true }));
            q('#lmForm').addEventListener('submit', (e) => { e.preventDefault(); this.saveSettings(); return false; });
        }

        // ----- dashboard ----------------------------------------------------------------------------------

        refreshStatus() {
            return getJson('LabelMaker/Status').then((s) => { this.status = s; this.renderStatus(s); }).catch(() => {
                this.view.querySelector('#lmBanner').textContent = 'Label Maker did not answer. Is Emby still starting?';
            });
        }

        renderStatus(s) {
            const view = this.view;
            const banner = view.querySelector('#lmBanner');
            const notes = [];
            if (s.Paused) { notes.push('Label Maker is paused: nothing is being looked at.'); }
            if (s.DryRun) { notes.push('Dry run is on: albums are matched and shown as Ready, but nothing is filed by itself. Turn it off in Settings > Checking (or with the button below) when the Ready list looks right.'); }
            if (s.Service && s.Service.MusicBrainz !== 'Ok') { notes.push('MusicBrainz has not answered since ' + when(s.Service.SinceUtc) + ', so albums wait and try again by themselves.'); }
            if (!(s.Lanes || []).length) { notes.push('There are no lanes yet. Add one in Settings > Lanes.'); }
            banner.textContent = notes.join(' ');
            banner.classList.toggle('hide', notes.length === 0);

            const tiles = view.querySelector('#lmTiles');
            clear(tiles);
            TILES.forEach(([key, label, cls]) => {
                const tile = el('div', 'lm-tile ' + cls);
                tile.appendChild(el('div', 'lm-count', String((s.Counts || {})[key] || 0)));
                tile.appendChild(el('div', 'lm-label', label));
                tiles.appendChild(tile);
            });

            const lanes = view.querySelector('#lmLanes');
            clear(lanes);
            (s.Lanes || []).forEach((lane) => {
                const box = el('div', 'lm-lane');
                box.appendChild(el('div', '', lane.LaneName || lane.LaneId));
                const counts = lane.Counts || {};
                const parts = ['NeedsReview', 'NoMatch', 'Ready', 'Waiting', 'Done', 'Failed'].filter(k => counts[k]).map(k => counts[k] + ' ' + (STATE_LABELS[k] || k).toLowerCase());
                box.appendChild(el('div', 'lm-small', parts.length ? parts.join(', ') : 'Nothing found yet.'));
                (lane.Problems || []).forEach((p) => {
                    const bad = el('div', 'lm-bad');
                    bad.appendChild(pill(p.Blocking ? 'Error' : 'Warning', p.Blocking ? 'Stopped' : 'Warning'));
                    bad.appendChild(document.createTextNode(' ' + p.Message));
                    box.appendChild(bad);
                });
                lanes.appendChild(box);
            });
            if (!(s.Lanes || []).length) { lanes.textContent = 'No lanes yet.'; }
            view.querySelector('#lmLastPass').textContent = s.LastPassUtc ? 'Last check: ' + when(s.LastPassUtc) + '.' : 'No check has run yet. The first one starts about 20 seconds after Emby starts.';
            view.querySelector('#lmPause span').textContent = s.Paused ? 'Resume' : 'Pause';
            view.querySelector('#lmDryToggle span').textContent = s.DryRun ? 'Turn dry run off' : 'Turn dry run on';
        }

        runNow() {
            const note = this.view.querySelector('#lmRunNote');
            note.textContent = 'Checking...';
            postJson('LabelMaker/Run').then(() => {
                note.textContent = 'Started. The numbers above update by themselves.';
                setTimeout(() => this.refreshStatus(), 1500);
            }).catch(() => { note.textContent = 'Could not start the check.'; });
        }

        togglePause() {
            const paused = !(this.status && this.status.Paused);
            postJson('LabelMaker/Pause', { Paused: paused }).then(() => { toast({ type: 'info', text: paused ? 'Paused.' : 'Resumed.' }); this.refreshStatus(); })
                .catch(() => toast({ type: 'error', text: 'Could not change the pause setting.' }));
        }

        toggleDryRun() {
            const dry = !(this.status && this.status.DryRun);
            if (!dry && !window.confirm('Turn dry run off? From now on albums that match with certainty are tagged, renamed and moved into your library without asking.')) { return; }
            postJson('LabelMaker/DryRun', { DryRun: dry }).then(() => { toast({ type: 'info', text: dry ? 'Dry run is on.' : 'Dry run is off.' }); this.refreshStatus(); })
                .catch(() => toast({ type: 'error', text: 'Could not change dry run.' }));
        }

        healthCheck() {
            const box = this.view.querySelector('#lmHealth');
            clear(box);
            box.textContent = 'Checking the folders...';
            loading.show();
            getJson('LabelMaker/Preflight').then((r) => {
                loading.hide();
                clear(box);
                (r.Issues || []).forEach((i) => { const d = el('div', 'lm-bad'); d.appendChild(pill('Error', 'Fix')); d.appendChild(document.createTextNode(' ' + i.Message)); box.appendChild(d); });
                (r.Lanes || []).forEach((lane) => {
                    const card = el('div', 'lm-lane');
                    card.appendChild(el('div', '', lane.LaneName + (lane.CanRun ? '' : ' (stopped until fixed)')));
                    (lane.Checks || []).forEach((c) => {
                        const line = el('div', 'lm-small');
                        line.appendChild(pill(c.Level, c.Level === 'Ok' ? 'OK' : c.Level));
                        line.appendChild(document.createTextNode(' ' + c.Message));
                        card.appendChild(line);
                    });
                    box.appendChild(card);
                });
                if (!(r.Lanes || []).length && !(r.Issues || []).length) { box.textContent = 'There are no lanes to check.'; }
            }).catch(() => { loading.hide(); box.textContent = 'The check did not finish.'; });
        }

        // ----- albums -------------------------------------------------------------------------------------

        loadAlbums() {
            const view = this.view;
            const laneSelect = view.querySelector('#lmFLane');
            const wantedLane = laneSelect.value;
            getJson('LabelMaker/Status').then((s) => {
                this.status = s;
                if (laneSelect.options.length !== (s.Lanes || []).length + 1) {
                    clear(laneSelect);
                    laneSelect.appendChild(new Option('All lanes', ''));
                    (s.Lanes || []).forEach(l => laneSelect.appendChild(new Option(l.LaneName || l.LaneId, l.LaneId)));
                    laneSelect.value = wantedLane;
                }
            });
            loading.show();
            const params = { State: view.querySelector('#lmFState').value, Lane: laneSelect.value, Text: view.querySelector('#lmFText').value, Start: this.page * PAGE_SIZE, Limit: PAGE_SIZE };
            getJson('LabelMaker/Albums', params).then((r) => {
                loading.hide();
                this.total = r.Total;
                this.renderAlbums(r);
            }).catch(() => { loading.hide(); toast({ type: 'error', text: 'Could not load the albums.' }); });
        }

        renderAlbums(r) {
            const view = this.view;
            const table = view.querySelector('#lmAlbumTable');
            clear(table);
            const head = el('tr');
            ['Album', 'Lane', 'State', 'Why', 'Best match', ''].forEach(h => head.appendChild(el('th', '', h)));
            table.appendChild(head);
            (r.Items || []).forEach((a) => {
                const tr = el('tr');
                const name = el('td');
                name.appendChild(el('div', '', (a.Artist || 'Unknown artist') + ' \u2013 ' + (a.Album || 'Unknown album')));
                name.appendChild(el('div', 'lm-small', plural(a.Files, 'file') + (a.Kind === 'Single' ? ', single track' : '')));
                tr.appendChild(name);
                tr.appendChild(el('td', '', a.LaneName));
                const state = el('td');
                state.appendChild(pill(a.State, a.Discarded ? 'Discarded' : null));
                tr.appendChild(state);
                tr.appendChild(el('td', 'lm-small', a.Reason || ''));
                const best = el('td', 'lm-small');
                if (a.BestTitle) { best.appendChild(el('div', '', a.BestTitle + (a.BestArtist ? ' \u2013 ' + a.BestArtist : ''))); best.appendChild(el('div', '', scoreWords(a.Score))); }
                tr.appendChild(best);
                const act = el('td');
                const open = el('button', 'raised emby-button', 'Open');
                open.type = 'button';
                open.addEventListener('click', () => this.openDetail(a.Id));
                act.appendChild(open);
                tr.appendChild(act);
                table.appendChild(tr);
            });
            view.querySelector('#lmAlbumTotal').textContent = r.Total === 0 ? 'Nothing here.' : plural(r.Total, 'album') + '.';
            const pages = Math.max(1, Math.ceil(r.Total / PAGE_SIZE));
            view.querySelector('#lmPageInfo').textContent = 'Page ' + (this.page + 1) + ' of ' + pages;
            view.querySelector('#lmPrev').disabled = this.page === 0;
            view.querySelector('#lmNext').disabled = (this.page + 1) * PAGE_SIZE >= r.Total;
        }

        openDetail(id) {
            loading.show();
            getJson('LabelMaker/Albums/' + encodeURIComponent(id)).then((a) => {
                loading.hide();
                this.openAlbum = a;
                this.renderDetail(a);
                this.view.querySelector('#lmDetail').scrollIntoView({ behavior: 'smooth', block: 'start' });
            }).catch(() => { loading.hide(); toast({ type: 'error', text: 'That album is not in the list any more.' }); this.loadAlbums(); });
        }

        closeDetail() {
            this.openAlbum = null;
            this.view.querySelector('#lmDetail').classList.add('hide');
        }

        renderDetail(a) {
            const view = this.view;
            view.querySelector('#lmDetail').classList.remove('hide');
            view.querySelector('#lmDetailTitle').textContent = (a.Artist || 'Unknown artist') + ' \u2013 ' + (a.Album || 'Unknown album');
            const stateLine = view.querySelector('#lmDetailState');
            clear(stateLine);
            stateLine.appendChild(pill(a.State, a.Discarded ? 'Discarded' : null));
            stateLine.appendChild(document.createTextNode(' ' + (a.Reason || '')));

            const buttons = view.querySelector('#lmDetailButtons');
            clear(buttons);
            const add = (label, handler, primary) => {
                const b = el('button', 'raised emby-button' + (primary ? ' button-submit' : ''), label);
                b.type = 'button';
                b.addEventListener('click', handler);
                buttons.appendChild(b);
            };
            const busy = a.State === 'Applying' || a.State === 'Matching';
            if (a.Discarded) { add('Restore from the trash', () => this.act(a.Id, 'Restore')); }
            else if (a.State === 'Done') { add('Undo (put it back in the inbox)', () => this.act(a.Id, 'Undo', 'Put every file back in the inbox with its old tags and names?')); }
            else if (!busy) {
                if (a.BestReleaseId && a.State !== 'Duplicate') { add('Use the best match', () => this.preview(a.BestReleaseId, null), true); }
                add('Keep my tags and file it', () => this.act(a.Id, 'KeepTags', 'File this album using the tags already in the files, unchanged?'));
                if (/holds \d+ different albums/.test(a.Reason || '')) { add('Split by album', () => this.act(a.Id, 'Split', 'Put each album in a folder of its own inside this folder?')); }
                add('Skip for now', () => this.act(a.Id, 'Skip'));
                add('Check again', () => this.act(a.Id, 'Recheck'));
                add('Discard (to the trash folder)', () => this.act(a.Id, 'Discard', 'Move this album to the _Trash folder of its inbox? Nothing is deleted.'));
            }
            add('Close', () => this.closeDetail());

            const body = view.querySelector('#lmDetailBody');
            clear(body);

            if ((a.FileList || []).length) {
                body.appendChild(el('h4', '', 'Your files'));
                const t = el('table', 'lm-table');
                const h = el('tr');
                ['File', 'Title', 'Artist', 'Album', 'Track', 'Disc'].forEach(x => h.appendChild(el('th', '', x)));
                t.appendChild(h);
                a.FileList.forEach((f) => {
                    const r = el('tr');
                    [f.File, f.Error || f.Title, f.Artist, f.Album, f.Track, f.Disc].forEach(v => r.appendChild(el('td', '', v === null || v === undefined ? '' : String(v))));
                    t.appendChild(r);
                });
                body.appendChild(t);
            }

            if (a.State !== 'Done' && !a.Discarded && !busy) {
                body.appendChild(el('h4', '', 'Pick the album'));
                if ((a.Candidates || []).length) { body.appendChild(this.candidateTable(a.Candidates, 'The editions Label Maker found')); }
                else { body.appendChild(el('div', 'lm-note', 'Label Maker found no edition that fits. Search by hand, paste a MusicBrainz link, or keep your own tags.')); }

                const search = el('div', 'lm-row');
                const artist = el('input'); artist.type = 'text'; artist.placeholder = 'Artist'; artist.value = a.Artist || '';
                const album = el('input'); album.type = 'text'; album.placeholder = 'Album'; album.value = a.Album || '';
                const go = el('button', 'raised emby-button', 'Search MusicBrainz'); go.type = 'button';
                go.addEventListener('click', () => this.search(artist.value, album.value));
                [artist, album, go].forEach(x => search.appendChild(x));
                body.appendChild(search);

                const paste = el('div', 'lm-row');
                const link = el('input'); link.type = 'text'; link.placeholder = 'Paste a MusicBrainz release or release group link, or its id'; link.style.minWidth = '28em';
                const look = el('button', 'raised emby-button', 'Look it up'); look.type = 'button';
                look.addEventListener('click', () => this.resolve(link.value));
                paste.appendChild(link); paste.appendChild(look);
                body.appendChild(paste);

                const found = el('div'); found.id = 'lmFound'; body.appendChild(found);
                const prev = el('div'); prev.id = 'lmPreview'; body.appendChild(prev);
            }
            if ((a.Notes || []).length) {
                const details = el('details');
                details.appendChild(el('summary', '', 'How Label Maker searched'));
                a.Notes.forEach(n => details.appendChild(el('div', 'lm-small', n)));
                body.appendChild(details);
            }
        }

        candidateTable(candidates, title) {
            const wrap = el('div');
            wrap.appendChild(el('div', 'lm-small', title));
            const t = el('table', 'lm-table');
            const h = el('tr');
            ['Edition', 'Artist', 'Country', 'Date', 'Fit', ''].forEach(x => h.appendChild(el('th', '', x)));
            t.appendChild(h);
            candidates.forEach((c) => {
                const r = el('tr');
                [c.Title, c.Artist, c.Country, c.Date, scoreWords(c.Score) + (c.WeakFiles ? ', ' + plural(c.WeakFiles, 'file') + ' not fitting' : '')].forEach(v => r.appendChild(el('td', '', v || '')));
                const td = el('td');
                const b = el('button', 'raised emby-button', 'Look at this edition'); b.type = 'button';
                b.addEventListener('click', () => this.preview(c.ReleaseId, null));
                td.appendChild(b); r.appendChild(td);
                t.appendChild(r);
            });
            wrap.appendChild(t);
            return wrap;
        }

        search(artist, album) {
            const id = this.openAlbum.Id;
            const token = ++this.findToken; // an older answer that arrives late must not replace a newer one
            loading.show();
            postJson('LabelMaker/Albums/' + encodeURIComponent(id) + '/Search', { Artist: artist, Album: album }).then((r) => {
                loading.hide();
                if (token === this.findToken) { this.showCandidates(r); }
            }).catch(() => { loading.hide(); toast({ type: 'error', text: 'The search did not finish.' }); });
        }

        resolve(text) {
            const id = this.openAlbum.Id;
            const token = ++this.findToken;
            loading.show();
            postJson('LabelMaker/Albums/' + encodeURIComponent(id) + '/Resolve', { Text: text }).then((r) => {
                loading.hide();
                if (token === this.findToken) { this.showCandidates(r); }
            }).catch(() => { loading.hide(); toast({ type: 'error', text: 'The lookup did not finish.' }); });
        }

        showCandidates(r) {
            const box = this.view.querySelector('#lmFound');
            clear(box);
            if (!r.Candidates || !r.Candidates.length) { box.appendChild(el('div', 'lm-note', r.Message || 'Nothing found.')); return; }
            box.appendChild(this.candidateTable(r.Candidates, 'What was found'));
        }

        preview(releaseId, mapping) {
            const id = this.openAlbum.Id;
            this.previewRelease = releaseId;
            loading.show();
            postJson('LabelMaker/Albums/' + encodeURIComponent(id) + '/Preview', { ReleaseId: releaseId, Mapping: mapping || [] }).then((p) => {
                loading.hide();
                this.renderPreview(p, mapping);
            }).catch(() => { loading.hide(); toast({ type: 'error', text: 'The preview did not finish.' }); });
        }

        renderPreview(p, mapping) {
            const box = this.view.querySelector('#lmPreview');
            if (!box) { return; }
            clear(box);
            box.classList.add('lm-card');
            if (!p.Success) { box.appendChild(el('div', 'lm-bad', p.Message || 'The preview did not work.')); return; }
            box.appendChild(el('h4', '', 'If you use \u201C' + p.Title + '\u201D by ' + p.Artist + ' (' + [p.Country, p.Date].filter(Boolean).join(' ') + ')'));
            box.appendChild(el('div', 'lm-small', 'Fit: ' + scoreWords(p.Score) + '. ' + (p.Destination ? 'It would go into the library folder \u201C' + p.Destination + '\u201D.' : '')));
            (p.Problems || []).forEach(x => box.appendChild(el('div', 'lm-bad', x)));
            const t = el('table', 'lm-table');
            const h = el('tr');
            ['Your file', 'Disc', 'Track', 'Track on the edition', 'Would be named', 'Fit'].forEach(x => h.appendChild(el('th', '', x)));
            t.appendChild(h);
            this.previewRows = [];
            (p.Files || []).forEach((f) => {
                const r = el('tr');
                r.appendChild(el('td', '', f.Source));
                const disc = el('input', 'lm-num'); disc.type = 'number'; disc.min = '1'; disc.value = f.Disc || '';
                const pos = el('input', 'lm-num'); pos.type = 'number'; pos.min = '1'; pos.value = f.Position || '';
                const d1 = el('td'); d1.appendChild(disc); r.appendChild(d1);
                const d2 = el('td'); d2.appendChild(pos); r.appendChild(d2);
                r.appendChild(el('td', '', f.Title));
                r.appendChild(el('td', 'lm-small', f.Destination));
                const fit = el('td'); fit.appendChild(f.Weak ? pill('Warning', 'Does not fit') : el('span', 'lm-good', f.Score.toFixed(2))); r.appendChild(fit);
                t.appendChild(r);
                this.previewRows.push({ file: f.Source, disc: disc, pos: pos, origDisc: String(f.Disc || ''), origPos: String(f.Position || '') });
            });
            box.appendChild(t);
            box.appendChild(el('div', 'lm-note', 'A file in the wrong place? Change its disc and track number, then press \u201CUpdate the preview\u201D.'));
            const row = el('div', 'lm-row');
            const update = el('button', 'raised emby-button', 'Update the preview'); update.type = 'button';
            update.addEventListener('click', () => this.preview(this.previewRelease, this.currentMapping()));
            const apply = el('button', 'raised emby-button button-submit', 'File the album with this edition'); apply.type = 'button';
            apply.disabled = !p.CanApply;
            apply.addEventListener('click', () => {
                if (!window.confirm('Tag the files with this edition and move them into your library? You can undo it afterwards.')) { return; }
                this.apply(this.previewRelease, this.currentMapping());
            });
            row.appendChild(update); row.appendChild(apply);
            box.appendChild(row);
        }

        currentMapping() {
            return (this.previewRows || []).filter(r => r.disc.value !== r.origDisc || r.pos.value !== r.origPos)
                .filter(r => r.disc.value && r.pos.value)
                .map(r => ({ File: r.file, Disc: parseInt(r.disc.value, 10), Position: parseInt(r.pos.value, 10) }));
        }

        apply(releaseId, mapping) {
            const id = this.openAlbum.Id;
            loading.show();
            postJson('LabelMaker/Albums/' + encodeURIComponent(id) + '/Apply', { ReleaseId: releaseId, Mapping: mapping || [] }).then((r) => {
                loading.hide();
                toast({ type: r.Success ? 'info' : 'error', text: r.Message || (r.Success ? 'Filed.' : 'Not filed.') });
                if (r.Success) { this.closeDetail(); }
                this.loadAlbums();
                this.refreshStatus();
            }).catch(() => { loading.hide(); toast({ type: 'error', text: 'The album was not filed.' }); });
        }

        act(id, action, question) {
            if (question && !window.confirm(question)) { return; }
            loading.show();
            postJson('LabelMaker/Albums/' + encodeURIComponent(id) + '/' + action).then((r) => {
                loading.hide();
                toast({ type: r.Success ? 'info' : 'error', text: r.Message || (r.Success ? 'Done.' : 'That did not work.') });
                if (r.Success) { this.closeDetail(); }
                this.loadAlbums();
                this.refreshStatus();
            }).catch(() => { loading.hide(); toast({ type: 'error', text: 'That did not work.' }); });
        }

        // ----- settings -----------------------------------------------------------------------------------

        loadSettings(message) {
            const view = this.view;
            const q = (s) => view.querySelector(s);
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then((c) => {
                loading.hide();
                this.config = c;
                clear(q('#lmLaneRows'));
                this.laneRows = [];
                (c.Lanes || []).forEach(l => this.addLaneRow({ Id: l.Id, Name: l.Name, InboxPath: l.InboxPath, LibraryPath: l.LibraryPath, AutoApply: l.AutoApply, isNew: false }));
                q('#lmDryRun').checked = !!c.DryRun;
                q('#lmPaused').checked = !!c.Paused;
                q('#lmPassSeconds').value = c.PassSeconds;
                q('#lmStableSeconds').value = c.StableSeconds;
                q('#lmMaxPerPass').value = c.MaxPerPass;
                q('#lmAuto').value = c.AutoThreshold;
                q('#lmReview').value = c.ReviewThreshold;
                q('#lmMinFile').value = c.MinFileScore;
                q('#lmMargin').value = c.Margin;
                q('#lmGroups').value = c.MaxReleaseGroups;
                q('#lmSameRec').checked = !!c.EditionsMustBeSameRecordings;
                q('#lmCountries').value = c.PreferredCountries || '';
                q('#lmContact').value = c.MusicBrainzContact || '';
                q('#lmScript').value = c.NamingScript || '';
                q('#lmId3').value = String(c.Id3Version || 4);
                q('#lmId3v1').checked = !!c.WriteId3v1;
                q('#lmAlertMaster').checked = !!c.SendPhoneAlerts;
                q('#lmAlertReview').checked = !!c.AlertNeedsReview;
                q('#lmAlertFailed').checked = !!c.AlertAlbumFailed;
                q('#lmAlertLane').checked = !!c.AlertLaneProblem;
                q('#lmAlertApplied').checked = !!c.AlertAutoApplied;
                q('#lmAlertTrouble').checked = !!c.AlertServiceTrouble;
                q('#lmAlertPass').checked = !!c.AlertPassFinished;
                q('#lmSaveNote').textContent = message || '';
            }).catch(() => { loading.hide(); toast({ type: 'error', text: 'Could not load the settings.' }); });
        }

        addLaneRow(lane) {
            const holder = this.view.querySelector('#lmLaneRows');
            const box = el('div', 'lm-lane');
            const fields = el('div', 'lm-lanefields');
            // A folder box has a Browse button that opens Emby's own folder picker on the server's file system.
            const field = (label, value, placeholder, browseHeader) => {
                const wrap = el('div');
                wrap.appendChild(el('div', 'lm-small', label));
                const input = el('input'); input.type = 'text'; input.value = value || ''; input.placeholder = placeholder || '';
                if (browseHeader) {
                    const row = el('div', 'lm-pathrow');
                    input.style.flex = '1 1 auto';
                    const browse = el('button', 'raised emby-button', 'Browse...'); browse.type = 'button';
                    browse.addEventListener('click', () => this.browseFolder(input, browseHeader));
                    row.appendChild(input); row.appendChild(browse);
                    wrap.appendChild(row);
                } else {
                    input.style.width = '100%';
                    wrap.appendChild(input);
                }
                fields.appendChild(wrap);
                return input;
            };
            const name = field('Name (what you call this lane)', lane.Name, 'For example Rock');
            const inbox = field('Inbox folder (where downloads arrive)', lane.InboxPath, 'Click Browse, or type a full path', 'Choose the inbox folder');
            const library = field('Library folder (where albums are filed)', lane.LibraryPath, 'Click Browse, or type a full path', 'Choose the library folder');
            box.appendChild(fields);
            const ask = el('label'); const cb = el('input'); cb.type = 'checkbox'; cb.checked = !lane.AutoApply;
            ask.appendChild(cb); ask.appendChild(document.createTextNode(' Always ask me first for this lane (nothing is filed without your approval on the Albums tab)'));
            box.appendChild(el('div', 'lm-row')).appendChild(ask);
            const row = el('div', 'lm-row');
            if (lane.Id) { row.appendChild(el('span', 'lm-small', 'Lane id: ' + lane.Id + ' (never changes)')); }
            const remove = el('button', 'raised emby-button', 'Remove this lane'); remove.type = 'button';
            row.appendChild(remove);
            box.appendChild(row);
            holder.appendChild(box);
            const entry = { lane: lane, name: name, inbox: inbox, library: library, ask: cb, box: box };
            this.laneRows.push(entry);
            remove.addEventListener('click', () => {
                if (!window.confirm('Remove this lane? Files in its folders are not touched.')) { return; }
                this.laneRows = this.laneRows.filter(x => x !== entry);
                box.parentNode.removeChild(box);
            });
        }

        // Emby's own folder picker (the one its library setup uses): it browses the server's drives and network shares and checks the folder can be written.
        browseFolder(input, header) {
            if (!window.Emby || typeof Emby.importModule !== 'function') {
                toast({ type: 'info', text: 'The folder picker is not available here. Type the full path instead.' });
                return;
            }
            Emby.importModule('./modules/directorybrowser/directorybrowser.js').then((DirectoryBrowser) => {
                const picker = new DirectoryBrowser();
                picker.show({
                    path: input.value.trim() || undefined,
                    header: header,
                    instruction: 'Pick a folder on the server that runs Emby. Label Maker needs permission to create, rename and delete files in it.',
                    validateWriteable: true,
                    callback: (path) => {
                        if (path) { input.value = path; }
                        picker.close();
                    }
                }).catch(() => { /* the dialog was closed without a choice */ });
            }).catch(() => toast({ type: 'info', text: 'The folder picker could not be opened. Type the full path instead.' }));
        }

        collectLanes() {
            const used = new Set();
            this.laneRows.forEach(r => { if (r.lane.Id) { used.add(r.lane.Id); } });
            return this.laneRows.map((r) => {
                let id = r.lane.Id;
                if (!id) {
                    const base = slug(r.name.value) || 'lane';
                    id = base;
                    for (let n = 2; used.has(id); n++) { id = base + '-' + n; }
                    used.add(id);
                }
                return { Id: id, Name: r.name.value.trim(), InboxPath: r.inbox.value.trim(), LibraryPath: r.library.value.trim(), AutoApply: !r.ask.checked };
            });
        }

        saveSettings() {
            const q = (s) => this.view.querySelector(s);
            const c = this.config;
            if (!c) { return; }
            c.Lanes = this.collectLanes();
            c.DryRun = q('#lmDryRun').checked;
            c.Paused = q('#lmPaused').checked;
            c.PassSeconds = parseInt(q('#lmPassSeconds').value, 10);
            c.StableSeconds = parseInt(q('#lmStableSeconds').value, 10);
            c.MaxPerPass = parseInt(q('#lmMaxPerPass').value, 10);
            c.AutoThreshold = parseFloat(q('#lmAuto').value);
            c.ReviewThreshold = parseFloat(q('#lmReview').value);
            c.MinFileScore = parseFloat(q('#lmMinFile').value);
            c.Margin = parseFloat(q('#lmMargin').value);
            c.MaxReleaseGroups = parseInt(q('#lmGroups').value, 10);
            c.EditionsMustBeSameRecordings = q('#lmSameRec').checked;
            c.PreferredCountries = q('#lmCountries').value;
            c.MusicBrainzContact = q('#lmContact').value;
            c.NamingScript = q('#lmScript').value;
            c.Id3Version = parseInt(q('#lmId3').value, 10);
            c.WriteId3v1 = q('#lmId3v1').checked;
            c.SendPhoneAlerts = q('#lmAlertMaster').checked;
            c.AlertNeedsReview = q('#lmAlertReview').checked;
            c.AlertAlbumFailed = q('#lmAlertFailed').checked;
            c.AlertLaneProblem = q('#lmAlertLane').checked;
            c.AlertAutoApplied = q('#lmAlertApplied').checked;
            c.AlertServiceTrouble = q('#lmAlertTrouble').checked;
            c.AlertPassFinished = q('#lmAlertPass').checked;
            loading.show();
            ApiClient.updatePluginConfiguration(pluginId, c).then(() => {
                loading.hide();
                toast({ type: 'info', text: 'Settings saved.' });
                this.loadSettings('Saved. Use "Check the folders" on the Dashboard to test the lanes.');
            }).catch((e) => {
                loading.hide();
                const show = (detail) => {
                    const text = detail || 'The settings were not saved. Check the values.';
                    toast({ type: 'error', text: text });
                    q('#lmSaveNote').textContent = text;
                };
                // Emby hands back the failed response; the server's sentence is its body.
                if (e && typeof e.text === 'function') { e.text().then((t) => show(this.errorText(t))).catch(() => show('')); } else { show(''); }
            });
        }

        errorText(text) {
            try { const j = JSON.parse(text); return j.ResponseStatus && j.ResponseStatus.Message ? j.ResponseStatus.Message : (j.Message || ''); }
            catch (x) { return String(text).substring(0, 300); }
        }

        // ----- lifecycle ----------------------------------------------------------------------------------

        onResume(options) {
            super.onResume(options);
            this.refreshStatus();
            this.pollTimer = setInterval(() => {
                if (!this.view.querySelector('#lmDashPage').classList.contains('hide')) { this.refreshStatus(); }
            }, 5000);
        }

        onPause() {
            super.onPause();
            if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = null; }
        }

        destroy() {
            if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = null; }
            super.destroy();
        }
    };
});
