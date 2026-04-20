// Placeholder (4-byte GIF) — forces Chromium to release decoded bitmaps.
const PLACEHOLDER = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';
const LIB_PAGE_SIZE = 50;

// State.
let _libTotal      = 0;
let _libHasMore    = false;
let _libLoading    = false;
let _libObserver   = null;  // unused — kept for destroyLibrary safety
let _libPage       = 0;     // current page, 0-indexed
let _libFiltered   = [];    // filtered + sorted master array (metadata only, no images)
let _libViewMode   = localStorage.getItem('libViewMode') || 'grid';  // 'grid' | 'folder'
let _libFolderPath = null;  // null = folder list view; string = subfolder contents

// Destroy / cleanup.
function destroyLibrary() {
    _libHasMore = false; // stop any in-flight _fetchNextMetaPage chain
    const g = document.getElementById('libGrid');
    if (g) {
        // Release decoded bitmaps BEFORE clearing DOM
        g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
        g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });
        g.innerHTML = '';
    }
    _setLibPaginator('');
    _libFiltered   = [];
    _libPage       = 0;
    _libTotal      = 0;
    _libHasMore    = false;
    _libLoading    = false;
    _libFolderPath = null;
}

// Data loading.
// First tab open: show localStorage cache immediately, then ask C# (returns
// instantly from its own in-memory cache after the first scan).
function refreshLibrary() {
    document.getElementById('libViewGrid')?.classList.toggle('active', _libViewMode === 'grid');
    document.getElementById('libViewFolder')?.classList.toggle('active', _libViewMode === 'folder');
    try {
        const raw = localStorage.getItem('vrcnext_lib_cache');
        if (raw) {
            const cached = JSON.parse(raw);
            if (cached.files && cached.files.length) {
                libraryFiles = cached.files;
                _libTotal    = cached.total || cached.files.length;
                _libHasMore  = false;
                filterLibrary();
            }
        }
    } catch {}
    sendToCS({ action: 'scanLibrary' });
}

// Refresh button: force a full filesystem rescan.
function forceRefreshLibrary() {
    _libHasMore  = false;
    _libLoading  = false;
    libraryFiles = [];
    _libFiltered = [];
    _libPage     = 0;
    _libTotal    = 0;
    const g = document.getElementById('libGrid');
    if (g) {
        g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
        g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });
        g.innerHTML = `<div class="empty-msg">${t('library.scanning', 'Scanning...')}</div>`;
    }
    _setLibPaginator('');
    sendToCS({ action: 'scanLibraryForce' });
}

function renderLibrary(data) {
    document.getElementById('libViewGrid')?.classList.toggle('active', _libViewMode === 'grid');
    document.getElementById('libViewFolder')?.classList.toggle('active', _libViewMode === 'folder');
    const files  = Array.isArray(data) ? data : (data.files || []);
    libraryFiles = files;
    _libTotal    = Array.isArray(data) ? files.length : (data.total || files.length);
    _libHasMore  = Array.isArray(data) ? false : (data.hasMore || false);
    _libLoading  = false;

    try {
        const cacheItems = files.slice(0, 100).map(x => ({
            name: x.name, path: x.path, folder: x.folder, type: x.type,
            size: x.size, modified: x.modified, time: x.time,
            url: x.url, worldId: x.worldId || '',
            players: (x.players || []).slice(0, 4),
        }));
        localStorage.setItem('vrcnext_lib_cache', JSON.stringify({ timestamp: Date.now(), files: cacheItems, total: _libTotal }));
    } catch {}

    _resolveWorldIds(files);
    filterLibrary();         // renders page 0
    _fetchNextMetaPage();    // eagerly load all remaining metadata (no scroll needed)
}

function appendLibraryPage(data) {
    const newFiles = data.files || [];
    _libTotal   = data.total || _libTotal;
    _libHasMore = data.hasMore || false;
    _libLoading = false;
    if (!newFiles.length) return;

    newFiles.forEach(f => libraryFiles.push(f));
    _resolveWorldIds(newFiles);

    // Apply current filters to new files and append to _libFiltered
    const ff   = document.getElementById('libFolderFilter')?.value ?? '__all__';
    const tf   = document.getElementById('libTypeFilter')?.value ?? 'all';
    let more   = newFiles;
    if (showFavOnly)      more = more.filter(x => favorites.has(x.path));
    if (ff !== '__all__') more = more.filter(x => x.folder === ff);
    if (tf !== 'all')     more = more.filter(x => x.type === tf);
    more.forEach(f => _libFiltered.push(f));
    _libFiltered.sort((a, b) => new Date(b.modified) - new Date(a.modified));

    // Update paginator to reflect the newly available total (grid mode only)
    if (_libViewMode !== 'folder') {
        const totalPages = Math.ceil(_libFiltered.length / LIB_PAGE_SIZE) || 1;
        _setLibPaginator(buildLibPagination(_libPage, totalPages));
    }

    // Continue chaining until all metadata is loaded
    _fetchNextMetaPage();
}

// Immediately requests the next metadata batch from C# — no scroll required.
// Chains automatically until _libHasMore is false.
function _fetchNextMetaPage() {
    if (!_libHasMore || _libLoading) return;
    _libLoading = true;
    sendToCS({ action: 'loadLibraryPage', offset: libraryFiles.length });
}

// Background enrichment: C# sends batches of { path → worldId } after the fast scan.
// Patches libraryFiles in-place and injects world badges into already-rendered cards.
function applyLibraryWorldIds(dict) {
    if (!dict || !Object.keys(dict).length) return;
    const newIds = [];
    for (const [path, worldId] of Object.entries(dict)) {
        if (!worldId) continue;
        // Patch in-memory item
        const item = libraryFiles.find(f => f.path === path);
        if (item && !item.worldId) { item.worldId = worldId; }
        if (!worldInfoCache[worldId]) newIds.push(worldId);
        // Patch visible card if rendered and badge not yet there
        const card = document.querySelector(`.lib-card[data-path="${CSS.escape(path)}"]`);
        if (!card) continue;
        const wrap = card.querySelector('.lib-thumb-wrap');
        if (!wrap || wrap.querySelector('.lib-world-badge')) continue;
        const wInfo = worldInfoCache[worldId];
        const wName  = wInfo ? esc(wInfo.name) : t('library.view_world', 'View World');
        const wThumb = wInfo?.thumbnailImageUrl || '';
        wrap.insertAdjacentHTML('beforeend',
            `<button class="lib-world-badge" data-wid="${esc(worldId)}" onclick="event.stopPropagation();openWorldSearchDetail('${esc(worldId)}')" title="${wName}"><span class="lib-world-badge-thumb" style="${wThumb ? `background-image:url('${cssUrl(wThumb)}')` : ''}"></span><span class="lib-world-badge-text">${wName}</span></button>`);
    }
    if (newIds.length) sendToCS({ action: 'vrcResolveWorlds', worldIds: [...new Set(newIds)].slice(0, 30) });
}

// Called when a new file lands in a watch folder — no rescan needed.
function addNewLibraryFile(item) {
    if (!item || libraryFiles.find(f => f.path === item.path)) return;
    libraryFiles.unshift(item); // prepend — newest first
    _resolveWorldIds([item]);
    filterLibrary(true); // re-filter current page so new file appears at top of page 0
}

function _resolveWorldIds(files) {
    const unknown = [...new Set((files || []).filter(x => x.worldId && !worldInfoCache[x.worldId]).map(x => x.worldId))];
    if (unknown.length > 0) sendToCS({ action: 'vrcResolveWorlds', worldIds: unknown.slice(0, 30) });
}

// Page rendering.
function _renderLibPage() {
    const g = document.getElementById('libGrid');
    if (!g) return;

    // Release decoded bitmaps from the previous page before clearing the DOM.
    // Setting src to the 4-byte placeholder is the only reliable way to free
    // bitmaps in Chromium — img.src='' and removeAttribute do NOT free them.
    g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
    g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });

    const start      = _libPage * LIB_PAGE_SIZE;
    const pageItems  = _libFiltered.slice(start, start + LIB_PAGE_SIZE);
    const totalPages = Math.ceil(_libFiltered.length / LIB_PAGE_SIZE) || 1;

    if (!pageItems.length) {
        const isFiltered = showFavOnly
            || (document.getElementById('libFolderFilter')?.value ?? '__all__') !== '__all__'
            || (document.getElementById('libTypeFilter')?.value ?? 'all') !== 'all';
        g.innerHTML = '<div class="empty-msg">' + (showFavOnly
            ? t('library.empty.favorites', 'No favorites yet.')
            : isFiltered
                ? t('library.empty.filtered', 'No media files found.')
                : t('library.empty.watch_folders', 'Add watch folders in Settings.')) + '</div>';
        _setLibPaginator('');
        return;
    }

    const groups = {};
    pageItems.forEach(x => {
        const d = new Date(x.modified);
        const k = fmtLongDate(d);
        if (!groups[k]) groups[k] = [];
        groups[k].push(x);
    });

    let h = '';
    for (const [dt, items] of Object.entries(groups)) {
        h += `<div class="lib-date-group-container" data-date="${esc(dt)}"><div class="lib-date-group">${esc(dt)}</div><div class="lib-date-group-cards">`;
        items.forEach(x => { h += _buildLibCard(x); });
        h += `</div></div>`;
    }
    g.innerHTML = h;

    _setLibPaginator(buildLibPagination(_libPage, totalPages));
}

// Paginator (1:1 from buildTlPagination / tlGoPage).
function buildLibPagination(page, totalPages) {
    if (totalPages <= 1) return '';
    const prevDis  = page === 0 ? 'disabled' : '';
    const nextDis  = page >= totalPages - 1 ? 'disabled' : '';
    const countInfo = `<span style="font-size:11px;color:var(--tx3);padding:0 8px;">${tf('library.pagination.files', { count: _libFiltered.length.toLocaleString() }, '{count} files')}</span>`;
    return `<button class="vrcn-button" ${prevDis} onclick="libGoPage(${page - 1})"><span class="msi" style="font-size:16px;">chevron_left</span></button>
        ${_buildPaginatorBtns(page, totalPages, 'libGoPage')}
        <button class="vrcn-button" ${nextDis} onclick="libGoPage(${page + 1})"><span class="msi" style="font-size:16px;">chevron_right</span></button>
        ${countInfo}`;
}

function libGoPage(page) {
    if (page < 0) return;
    const totalPages = Math.ceil(_libFiltered.length / LIB_PAGE_SIZE) || 1;
    if (page >= totalPages) return;
    if (page === _libPage) return;
    _libPage = page;
    _renderLibPage();
    const wrap = document.querySelector('.lib-wrap');
    if (wrap) wrap.scrollTop = 0;
}

function _setLibPaginator(html) {
    const bar = document.getElementById('libPaginatorBar');
    if (bar) bar.innerHTML = html;
}

// Filter.
// keepPage=true: stay on current page (delete / favorite / hide actions)
// keepPage=false (default): reset to page 0 (filter/sort changes)
function filterLibrary(keepPage = false) {
    const ff         = document.getElementById('libFolderFilter').value;
    const typeFilter = document.getElementById('libTypeFilter').value;
    let f            = [...libraryFiles]; // always a copy — never share ref with libraryFiles
    if (showFavOnly)      f = f.filter(x => favorites.has(x.path));
    if (ff !== '__all__') f = f.filter(x => x.folder === ff);
    if (typeFilter !== 'all') f = f.filter(x => x.type === typeFilter);
    f.sort((a, b) => new Date(b.modified) - new Date(a.modified));

    _libFiltered = f;
    if (!keepPage) _libPage = 0;
    // Clamp page in case items were removed and total pages shrank
    const totalPages = Math.ceil(_libFiltered.length / LIB_PAGE_SIZE) || 1;
    if (_libPage >= totalPages) _libPage = totalPages - 1;

    if (_libViewMode === 'folder') {
        if (_libFolderPath) {
            _renderFolderContents();
        } else {
            _renderFolderView();
        }
    } else {
        _renderLibPage();
    }
}

// Folder mode.
function setLibViewMode(mode) {
    _libViewMode   = mode;
    _libFolderPath = null;
    localStorage.setItem('libViewMode', mode);
    document.getElementById('libViewGrid')?.classList.toggle('active', mode === 'grid');
    document.getElementById('libViewFolder')?.classList.toggle('active', mode === 'folder');
    _updateLibBreadcrumb();
    filterLibrary();
}

function _updateLibBreadcrumb() {
    const bc = document.getElementById('libFolderBreadcrumb');
    if (!bc) return;
    if (_libViewMode === 'folder' && _libFolderPath) {
        const name = _libFolderPath.split(/[\\/]/).pop() || _libFolderPath;
        const nameEl = document.getElementById('libFolderBreadcrumbName');
        if (nameEl) nameEl.textContent = name;
        bc.style.display = 'flex';
    } else {
        bc.style.display = 'none';
    }
}

// Returns the full path of the immediate parent directory for a file.
function _getFileSubfolderPath(x) {
    const fp   = x.path || '';
    const last = Math.max(fp.lastIndexOf('/'), fp.lastIndexOf('\\'));
    return last > 0 ? fp.substring(0, last) : (x.folder || '');
}

function _renderFolderView() {
    const g = document.getElementById('libGrid');
    if (!g) return;
    g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
    g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });
    _setLibPaginator('');

    if (!_libFiltered.length) {
        g.innerHTML = '<div class="empty-msg">' + t('library.empty.watch_folders', 'Add watch folders in Settings.') + '</div>';
        return;
    }

    // Group files by immediate parent directory
    const groups = {};
    _libFiltered.forEach(x => {
        const sub = _getFileSubfolderPath(x);
        if (!groups[sub]) groups[sub] = [];
        groups[sub].push(x);
    });

    // Sort groups by most-recent file descending
    const sorted = Object.entries(groups).sort((a, b) => {
        const latestA = a[1].reduce((mx, f) => Math.max(mx, new Date(f.modified).getTime()), 0);
        const latestB = b[1].reduce((mx, f) => Math.max(mx, new Date(f.modified).getTime()), 0);
        return latestB - latestA;
    });

    let h = '<div class="lib-date-group-cards">';
    for (const [subPath, files] of sorted) {
        h += _buildFolderCard(subPath, files);
    }
    h += '</div>';
    g.innerHTML = h;
}

function _buildFolderCard(subPath, files) {
    const name     = subPath.split(/[\\/]/).pop() || subPath;
    const previews = files.filter(x => x.type === 'image').slice(0, 4);
    const count    = files.length;
    const sp       = jsq(subPath);

    let slots = '';
    for (let i = 0; i < 4; i++) {
        const p = previews[i];
        if (p && p.url) {
            const blurStyle = hiddenMedia.has(p.path) ? ' style="filter:blur(20px);transform:scale(1.08);"' : '';
            slots += `<img src="${esc(p.url)}?thumb=1" loading="lazy"${blurStyle} onerror="this.className='lib-folder-preview-slot'">`;
        } else {
            slots += `<div class="lib-folder-preview-slot"></div>`;
        }
    }

    const countLabel = count === 1
        ? t('library.folder.one_file', '1 file')
        : tf('library.folder.file_count', { count }, '{count} files');
    return `<div class="lib-card" style="cursor:pointer;" onclick="_openLibFolder('${sp}')"><div class="lib-folder-preview">${slots}</div><div class="lib-info"><div class="lib-name">${esc(name)}</div><div class="lib-meta"><span>${esc(countLabel)}</span></div></div></div>`;
}

function _openLibFolder(subPath) {
    _libFolderPath = subPath;
    _updateLibBreadcrumb();
    _renderFolderContents();
}

function _backToFolderList() {
    _libFolderPath = null;
    _updateLibBreadcrumb();
    _renderFolderView();
}

function _renderFolderContents() {
    const g = document.getElementById('libGrid');
    if (!g) return;
    g.querySelectorAll('.lib-thumb').forEach(img => { img.src = PLACEHOLDER; });
    g.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });
    _setLibPaginator('');

    const files = _libFiltered.filter(x => _getFileSubfolderPath(x) === _libFolderPath);
    if (!files.length) {
        g.innerHTML = '<div class="empty-msg">' + t('library.empty.filtered', 'No media files found.') + '</div>';
        return;
    }

    const groups = {};
    files.forEach(x => {
        const k = fmtLongDate(new Date(x.modified));
        if (!groups[k]) groups[k] = [];
        groups[k].push(x);
    });

    let h = '';
    for (const [dt, items] of Object.entries(groups)) {
        h += `<div class="lib-date-group-container" data-date="${esc(dt)}"><div class="lib-date-group">${esc(dt)}</div><div class="lib-date-group-cards">`;
        items.forEach(x => { h += _buildLibCard(x); });
        h += `</div></div>`;
    }
    g.innerHTML = h;
}

// Resolution tag.
function _resTag(x) {
    const h = x.imgH || 0;
    if (!h) return '';
    if (h <= 720)  return 'SD';
    if (h <= 1080) return 'HD';
    if (h <= 1440) return '2K';
    if (h <= 2160) return '4K';
    return '8K';
}

// Card building.
function _buildLibCard(x) {
    const su     = x.url || '';
    const suAttr = esc(su);
    const suJs   = jsq(su);
    const sp     = jsq(x.path || '');
    const sn     = jsq(x.name || '');
    const iF     = favorites.has(x.path),  fc = iF ? ' active' : '';
    const iH     = hiddenMedia.has(x.path), hc = iH ? ' active' : '';
    const ac     = ['lib-actions', iF ? 'has-fav' : '', iH ? 'has-hidden' : ''].filter(Boolean).join(' ');
    const acts   = `<div class="${ac}"><button class="vrcn-lib-button clip" onclick="event.stopPropagation();copyToClipboard('${suJs}','${sp}','${x.type}')" title="${esc(t('library.actions.copy_clipboard', 'Copy to clipboard'))}"><span class="msi" style="font-size:16px;">content_copy</span></button><button class="vrcn-lib-button fav${fc}" onclick="event.stopPropagation();toggleFavorite('${sp}')" title="${esc(t('library.actions.favorite', 'Favorite'))}"><span class="msi" style="font-size:16px;">star</span></button><button class="vrcn-lib-button hide${hc}" onclick="event.stopPropagation();toggleHidden('${sp}')" title="${esc(iH ? t('library.actions.unhide', 'Unhide') : t('library.actions.hide', 'Hide'))}"><span class="msi" style="font-size:16px;">${iH ? 'visibility' : 'visibility_off'}</span></button><button class="vrcn-lib-button del" onclick="event.stopPropagation();showDeleteModal('${sp}','${sn}')"><span class="msi" style="font-size:16px;">delete</span></button></div>`;
    const blurClass = iH ? ' lib-blurred' : '';
    const idx       = libraryFiles.indexOf(x);

    if (x.type === 'image') {
        let worldBadge = '';
        if (x.worldId) {
            const wInfo  = worldInfoCache[x.worldId];
            const wName  = wInfo ? esc(wInfo.name) : t('library.view_world', 'View World');
            const wThumb = wInfo?.thumbnailImageUrl || '';
            worldBadge   = `<button class="lib-world-badge" data-wid="${esc(x.worldId)}" onclick="event.stopPropagation();openWorldSearchDetail('${esc(x.worldId)}')" title="${wName}"><span class="lib-world-badge-thumb" style="${wThumb ? `background-image:url('${cssUrl(wThumb)}')` : ''}"></span><span class="lib-world-badge-text">${wName}</span></button>`;
        }
        let playersOverlay = '';
        const players = x.players || [];
        if (players.length > 0) {
            const show      = players.slice(0, 3);
            const remaining = players.length - show.length;
            playersOverlay  = `<div class="lib-players-overlay" onclick="event.stopPropagation();openPhotoDetail(${idx})">` +
                show.map(p => {
                    const fr  = vrcFriendsData.find(f => f.id === p.userId);
                    const img = fr?.image || p.image || '';
                    return img
                        ? `<div class="lib-player-av" style="background-image:url('${cssUrl(img)}')" title="${esc(p.displayName)}"></div>`
                        : `<div class="lib-player-av lib-player-av-letter" title="${esc(p.displayName)}">${esc((p.displayName||'?')[0])}</div>`;
                }).join('') +
                (remaining > 0 ? `<div class="lib-player-av lib-player-av-more">+${remaining}</div>` : '') +
                `</div>`;
        }
        const thumbSrc = suAttr ? suAttr + '?thumb=1' : '';
        const resTag   = _resTag(x);
        const resBadge = resTag ? `<span class="vrcn-badge accent" style="margin-left:4px;">${resTag}</span>` : '';
        return `<div class="lib-card" data-path="${esc(x.path||'')}" data-url="${suAttr}" data-type="image" data-name="${esc(x.name||'')}">${acts}<div class="lib-thumb-wrap${blurClass}" onclick="openLightbox('${suJs}','image')"><img class="lib-thumb" src="${thumbSrc}" loading="lazy" onerror="this.outerHTML='<div style=\\'width:100%;height:100%;display:flex;align-items:center;justify-content:center;color:var(--tx3);font-size:11px;font-weight:700\\'>${jsq(t('library.no_preview', 'No Preview'))}</div>'">${iH ? '<div class="lib-blur-hint"><span class="msi" style="font-size:18px;">visibility_off</span></div>' : ''}${worldBadge}${playersOverlay}</div><div class="lib-info" onclick="event.stopPropagation();openPhotoDetail(${idx})" style="cursor:pointer;"><div class="lib-name">${esc(x.name)}</div><div class="lib-meta"><span style="display:flex;align-items:center;">${x.size}${resBadge}</span><span>${x.time}</span></div></div></div>`;
    } else {
        const ck     = x.path || '';
        const cached = thumbCache[ck];
        const th     = cached
            ? `<img class="lib-thumb" src="${cached}">`
            : `<video class="lib-vid-thumb-video" src="${suAttr}" preload="metadata" muted onloadeddata="cacheVidThumb(this,'${sp}')" onerror="this.outerHTML='<div class=\\'lib-vid-thumb-fallback\\'>${jsq(t('library.video_badge', 'VIDEO'))}</div>'"></video>`;
        return `<div class="lib-card" data-path="${esc(x.path||'')}" data-url="${suAttr}" data-type="video" data-name="${esc(x.name||'')}">${acts}<div class="lib-thumb-wrap${blurClass}" onclick="openLightbox('${suJs}','video')">${th}<div class="lib-vid-overlay"><div class="lib-play-icon"><span class="msi" style="font-size:22px;">play_arrow</span></div></div><span class="lib-vid-badge">${t('library.video_badge', 'VIDEO')}</span>${iH ? '<div class="lib-blur-hint"><span class="msi" style="font-size:18px;">visibility_off</span></div>' : ''}</div><div class="lib-info"><div class="lib-name">${esc(x.name)}</div><div class="lib-meta"><span>${x.size}</span><span>${x.time}</span></div></div></div>`;
    }
}

// World info.
function onWorldsResolved(dict) {
    if (!dict || typeof dict !== 'object') return;
    Object.entries(dict).forEach(([id, w]) => {
        worldInfoCache[id] = { id, name: w.name || '', thumbnailImageUrl: w.thumbnailImageUrl || w.imageUrl || '' };
    });
    Object.assign(dashWorldCache, dict);
    renderDashboard();
    if (typeof renderVrcFriends === 'function' && vrcFriendsData?.length) renderVrcFriends(vrcFriendsData);
    document.querySelectorAll('.lib-world-badge[data-wid]').forEach(btn => {
        const wid  = btn.getAttribute('data-wid');
        const info = worldInfoCache[wid];
        if (info) {
            const thumbEl = btn.querySelector('.lib-world-badge-thumb');
            const textEl  = btn.querySelector('.lib-world-badge-text');
            if (thumbEl && info.thumbnailImageUrl) thumbEl.style.backgroundImage = `url('${info.thumbnailImageUrl}')`;
            if (textEl) textEl.textContent = info.name || t('library.view_world', 'View World');
        }
    });
}

// Folder filter.
function updateFolderFilterOptions(fs) {
    const s = document.getElementById('libFolderFilter'), c = s.value;
    s.innerHTML = `<option value="__all__">${t('library.filters.all_folders', 'All Folders')}</option>`;
    (fs || []).forEach(f => {
        const n = f.split(/[\\\\/]/).pop() || f;
        s.innerHTML += `<option value="${esc(f)}">${esc(n)}</option>`;
    });
    s.value = c || '__all__';
    if (s._vnRefresh) s._vnRefresh();
}

// Favorites / hidden.
function toggleFavFilter() {
    showFavOnly = !showFavOnly;
    document.getElementById('libFavBtn').classList.toggle('active', showFavOnly);
    filterLibrary();
}

function toggleFavorite(p) {
    if (favorites.has(p)) {
        favorites.delete(p);
        sendToCS({ action: 'removeFavorite', path: p });
    } else {
        favorites.add(p);
        sendToCS({ action: 'addFavorite', path: p });
    }
    filterLibrary(true); // stay on current page
}

function toggleHidden(p) {
    if (hiddenMedia.has(p)) {
        hiddenMedia.delete(p);
    } else {
        hiddenMedia.add(p);
    }
    try { localStorage.setItem('vrcnext_hidden', JSON.stringify([...hiddenMedia])); } catch {}
    filterLibrary(true); // stay on current page
    renderDashRecentPhotos();
}

function setLibItemAsDashBg(path) {
    dashBgPath = path;
    dashBgDataUri = '';
    const nameEl = document.getElementById('dashBgName');
    if (nameEl) nameEl.textContent = path.split(/[/\\]/).pop();
    renderDashboard();
    autoSave();
    showToast(true, t('library.background_updated', 'Background updated'));
}

// Video thumbnail.
function cacheVidThumb(v, fp) {
    try {
        v.currentTime = 1;
        v.addEventListener('seeked', function () {
            const c = document.createElement('canvas');
            c.width  = v.videoWidth  || 320;
            c.height = v.videoHeight || 240;
            c.getContext('2d').drawImage(v, 0, 0, c.width, c.height);
            const data = c.toDataURL('image/jpeg', 0.7);
            thumbCache[fp] = data;
            const img = document.createElement('img');
            img.className = 'lib-thumb';
            img.src = data;
            v.replaceWith(img);
            try { v.pause(); } catch (e) {}
            v.removeAttribute('src');
            try { v.load(); } catch (e) {}
        }, { once: true });
    } catch (e) {}
}

// Photo detail modal.
function openPhotoDetail(idx) {
    const x = libraryFiles[idx];
    if (!x) return;
    const el      = document.getElementById('detailModalContent');
    const imgUrl  = x.url || '';
    const players = x.players || [];
    const worldId = x.worldId || '';
    const wInfo   = worldId ? worldInfoCache[worldId] : null;
    const worldName = wInfo?.name || worldId || '';
    const date    = new Date(x.modified);
    const dateStr = fmtLongDate(date);
    const timeStr = fmtTime(date);

    // Use thumbnail for banner — avoids loading full-res image (30-100 MB) into RAM for a modal
    const thumbUrl  = imgUrl ? imgUrl + '?thumb=1' : '';
    const bannerHtml = thumbUrl ? `<div class="fd-banner"><img src="${thumbUrl}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>` : '';

    let metaHtml = `<div class="fd-meta">
        <div class="fd-meta-row"><span class="fd-meta-label">${t('library.detail.date', 'Date')}</span><span>${esc(dateStr)}</span></div>
        <div class="fd-meta-row"><span class="fd-meta-label">${t('library.detail.time', 'Time')}</span><span>${esc(timeStr)}</span></div>
        <div class="fd-meta-row"><span class="fd-meta-label">${t('library.detail.size', 'Size')}</span><span>${esc(x.size)}</span></div>`;
    if (worldName) {
        metaHtml += `<div class="fd-meta-row" style="cursor:pointer;" onclick="document.getElementById('modalDetail').style.display='none';openWorldSearchDetail('${esc(worldId)}')"><span class="fd-meta-label">${t('library.detail.world', 'World')}</span><span style="color:var(--accent-lt);">${esc(worldName)}</span></div>`;
    }
    metaHtml += `</div>`;

    let playersHtml = '';
    if (players.length > 0) {
        playersHtml = `<div style="font-size:10px;font-weight:700;color:var(--tx3);padding:8px 0 4px;letter-spacing:.05em;">${tf('library.detail.players_title', { count: players.length }, 'PLAYERS IN INSTANCE ({count})')}</div><div class="photo-players-list">`;
        players.forEach(p => {
            const onclick = p.userId ? `document.getElementById('modalDetail').style.display='none';openFriendDetail('${jsq(p.userId)}')` : '';
            playersHtml += renderProfileItemSmall({ id: p.userId, displayName: p.displayName, image: p.image }, onclick);
        });
        playersHtml += `</div>`;
    }

    el.innerHTML = `${bannerHtml}<div class="fd-content${imgUrl ? ' fd-has-banner' : ''}" style="padding:20px;">
        <h2 style="margin:0 0 12px;color:var(--tx0);font-size:16px;">${esc(x.name)}</h2>
        ${metaHtml}${playersHtml}
        <div style="margin-top:14px;text-align:right;"><button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">${t('common.close', 'Close')}</button></div>
    </div>`;
    document.getElementById('modalDetail').style.display = 'flex';
}

// Lightbox.
function openLightbox(u, t) {
    const lb    = document.createElement('div');
    lb.className = 'lib-lightbox';
    const closeLb = () => {
        // Clear src BEFORE removing element to release decoded bitmaps
        lb.querySelectorAll('img').forEach(img => { img.src = PLACEHOLDER; });
        lb.querySelectorAll('video').forEach(v => { try { v.pause(); } catch {} v.src = ''; });
        lb.remove();
        document.removeEventListener('keydown', ok);
    };
    lb.onclick = e => { if (e.target === lb) closeLb(); };
    if (t === 'video') {
        const v    = document.createElement('video');
        v.src      = u;
        v.controls = true;
        v.autoplay = true;
        v.style.cssText = 'max-width:90%;max-height:90%;border-radius:8px;';
        v.onclick  = e => e.stopPropagation();
        lb.appendChild(v);
    } else {
        lb.innerHTML = `<img src="${u}">`;
    }
    document.body.appendChild(lb);
    const ok = e => { if (e.key === 'Escape') closeLb(); };
    document.addEventListener('keydown', ok);
}

// Delete modal.
function showDeleteModal(fp, fn) {
    pendingDeletePath = fp;
    const x = document.getElementById('deleteModal');
    if (x) x.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.id        = 'deleteModal';
    o.onclick   = e => { if (e.target === o) closeDeleteModal(); };
    o.innerHTML = `<div class="modal-box"><div class="modal-icon danger"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-title">${t('library.delete.title', 'Delete File')}</div><div class="modal-msg">${t('library.delete.message', 'Permanently delete from disk:')}<br><span class="modal-fname">${esc(fn)}</span></div><div class="modal-btns"><button id="libDelCancelBtn" class="vrcn-button-round" onclick="closeDeleteModal()">${t('common.cancel', 'Cancel')}</button><button class="vrcn-button-round vrcn-btn-danger" onclick="confirmDelete()">${t('library.delete.confirm', 'Delete')}</button></div></div>`;
    document.body.appendChild(o);
    o.querySelector('#libDelCancelBtn').focus();
    const ok = e => {
        if (e.key === 'Escape') { closeDeleteModal(); document.removeEventListener('keydown', ok); }
        if (e.key === 'Enter')  { confirmDelete();    document.removeEventListener('keydown', ok); }
    };
    document.addEventListener('keydown', ok);
}

function closeDeleteModal() {
    pendingDeletePath = null;
    const m = document.getElementById('deleteModal');
    if (m) m.remove();
}

function confirmDelete() {
    if (pendingDeletePath) {
        sendToCS({ action: 'deleteLibraryFile', path: pendingDeletePath });
        favorites.delete(pendingDeletePath);
    }
    closeDeleteModal();
}

function showDeleteAllModal() {
    if (!postedFiles.length) return;
    const x = document.getElementById('deleteModal');
    if (x) x.remove();
    const o = document.createElement('div');
    o.className = 'modal-overlay';
    o.id        = 'deleteModal';
    o.onclick   = e => { if (e.target === o) closeDeleteModal(); };
    o.innerHTML = `<div class="modal-box"><div class="modal-icon danger"><span class="msi" style="font-size:22px;">delete</span></div><div class="modal-title">${t('library.delete_all.title', 'Delete All Posts')}</div><div class="modal-msg">${tf('library.delete_all.message', { count: postedFiles.length }, 'Delete all {count} post(s) from Discord?')}</div><div class="modal-btns"><button class="vrcn-button-round" onclick="closeDeleteModal()">${t('common.cancel', 'Cancel')}</button><button class="vrcn-button-round vrcn-btn-danger" onclick="confirmDeleteAll()">${t('library.delete_all.confirm', 'Delete All')}</button></div></div>`;
    document.body.appendChild(o);
}

function confirmDeleteAll() {
    postedFiles.forEach(f => {
        if (f.messageId) sendToCS({ action: 'deletePost', messageId: f.messageId, webhookUrl: f.webhookUrl });
    });
    closeDeleteModal();
}

// Clipboard.
function copyToClipboard(_url, path, type) {
    if (type === 'image') {
        sendToCS({ action: 'copyImageToClipboard', path });
    } else {
        navigator.clipboard.writeText(path).then(
            () => showToast(true, t('library.clipboard.success', 'Path copied to clipboard')),
            () => showToast(false, t('library.clipboard.failed', 'Clipboard copy failed'))
        );
    }
}
