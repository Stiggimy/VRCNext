/* === Dashboard === */

let _dashOnlineCount = 0;
let _dashOnlineCountLastFetch = 0;

function getDashFriendCountLabel(count, keyBase) {
    return count === 1
        ? tf(`${keyBase}.one`, { count }, '{count} friend')
        : tf(`${keyBase}.other`, { count }, '{count} friends');
}

function updateDashSub() {
    const name = currentVrcUser?.displayName;
    const status = name
        ? (currentVrcUser.statusDescription || statusLabel(currentVrcUser.status))
        : t('dashboard.sub.connect_world', 'Connect to VRChat to see your world');
    const suffix = _dashOnlineCount > 0
        ? ` | ${tf('dashboard.sub.playing_worldwide', { count: _dashOnlineCount.toLocaleString() }, '{count} playing worldwide')}`
        : '';
    document.getElementById('dashSub').textContent = status + suffix;
}

function renderDashboard() {
    const name = currentVrcUser?.displayName;
    document.getElementById('dashWelcome').textContent = name
        ? tf('dashboard.welcome.named', { name }, 'Welcome, {name}!')
        : t('dashboard.welcome.default', 'Welcome!');
    updateDashSub();

    const bgEl = document.getElementById('dashHeroBg');
    if (dashBgDataUri) {
        bgEl.style.backgroundImage = `url('${dashBgDataUri}')`;
    } else if (dashBgPath) {
        const fileUri = 'file:///' + dashBgPath.replace(/\\/g, '/');
        bgEl.style.backgroundImage = `url('${fileUri}')`;
    } else {
        bgEl.style.backgroundImage = `url('fallback_bg.png')`;
    }
    const fadeEl = document.querySelector('.dash-hero-fade');
    if (fadeEl) {
        const op = dashOpacity / 100;
        fadeEl.style.background = `linear-gradient(to bottom, rgba(0,0,0,${op * 0.4}) 0%, var(--bg-base) 100%)`;
    }
    if (currentSpecialTheme === 'auto') applyAutoColor();

    renderDashWorlds();
    renderDashFriendsFeed();
    renderDashFavWorlds();
    renderDashRecentlyVisited();
    renderDashPopularWorlds();
    renderDashActiveWorlds();
    renderDashGroupActivity();
    renderDashRecentTimeline();
    const now = Date.now();
    if (now - _dashOnlineCountLastFetch >= 10 * 60 * 1000) {
        _dashOnlineCountLastFetch = now;
        sendToCS({ action: 'vrcGetOnlineCount' });
    }
}

function requestWorldResolution() {
    if (!vrcFriendsData.length) return;
    const worldIds = new Set();
    const groupIds = new Set();
    vrcFriendsData.forEach(f => {
        const { worldId, ownerId } = parseFriendLocation(f.location);
        if (worldId && worldId.startsWith('wrld_') && !dashWorldCache[worldId]) worldIds.add(worldId);
        if (ownerId && ownerId.startsWith('grp_') && !dashGroupCache[ownerId]) groupIds.add(ownerId);
    });
    if (worldIds.size > 0) {
        sendToCS({ action: 'vrcResolveWorlds', worldIds: Array.from(worldIds) });
    }
    if (groupIds.size > 0) {
        sendToCS({ action: 'vrcResolveGroups', groupIds: Array.from(groupIds) });
    }
}

function renderDashWorlds() {
    const el = document.getElementById('dashFavWorlds');
    if (!currentVrcUser || !vrcFriendsLoaded) {
        el.innerHTML = sk('world', 3);
        return;
    }
    if (!vrcFriendsData.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.empty', 'No friends in worlds right now')}</div>`;
        return;
    }

    // Group friends by worldId parsed from location
    const worlds = {};
    vrcFriendsData.forEach(f => {
        const { worldId, instanceType } = parseFriendLocation(f.location);
        if (!worldId || !worldId.startsWith('wrld_')) return;

        if (!worlds[worldId]) {
            const cached = dashWorldCache[worldId];
            worlds[worldId] = {
                worldId: worldId,
                name: cached?.name || null,
                thumb: cached?.thumbnailImageUrl || cached?.imageUrl || '',
                instanceType: instanceType,
                friends: [],
                location: f.location,
                instances: new Set()
            };
        }
        worlds[worldId].friends.push(f);
        worlds[worldId].instances.add(f.location);
    });

    const worldList = Object.values(worlds);
    if (!worldList.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.empty', 'No friends in worlds right now')}</div>`;
        return;
    }

    // Check if we have unresolved worlds
    const unresolved = worldList.filter(w => !w.name);
    if (unresolved.length > 0 && Object.keys(dashWorldCache).length === 0) {
        // Show placeholder while resolving
        el.innerHTML = worldList.map(w => {
            const friendAvatars = w.friends.slice(0, 5).map(f => {
                const img = f.image || '';
                return img
                    ? `<img class="cc-friend-av" src="${img}" title="${esc(f.displayName)}" onerror="this.style.display='none'">`
                    : `<div class="cc-friend-av" title="${esc(f.displayName)}" style="display:flex;align-items:center;justify-content:center;font-size:9px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
            }).join('');
            const extra = w.friends.length > 5 ? `<span class="cc-extra">+${w.friends.length - 5}</span>` : '';
            const friendCountLabel = getDashFriendCountLabel(w.friends.length, 'dashboard.worlds.count_world');
            return `<div class="vrcn-content-card">
                <div class="cc-bg"></div>
                <div class="cc-scrim"></div>
                <div class="cc-content">
                    <div class="cc-name" style="color:var(--tx3)">${t('dashboard.worlds.loading_world', 'Loading world...')}</div>
                    <div class="cc-friends-row">${friendAvatars}${extra}</div>
                    <div class="cc-bottom-row">
                        <div class="cc-meta"><span class="msi">person</span>${friendCountLabel}</div>
                    </div>
                </div>
            </div>`;
        }).join('');
        return;
    }

    el.innerHTML = worldList.map(w => {
        const friendAvatars = w.friends.slice(0, 5).map(f => {
            const img = f.image || '';
            return img
                ? `<img class="cc-friend-av" src="${img}" title="${esc(f.displayName)}" onerror="this.style.display='none'">`
                : `<div class="cc-friend-av" title="${esc(f.displayName)}" style="display:flex;align-items:center;justify-content:center;font-size:9px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
        }).join('');
        const extra = w.friends.length > 5 ? `<span class="cc-extra">+${w.friends.length - 5}</span>` : '';
        const thumbStyle = w.thumb ? `background-image:url('${cssUrl(w.thumb)}')` : '';
        const wid = (w.worldId || '').replace(/'/g, "\\'");
        const displayName = w.name || w.worldId;
        const instCount = w.instances ? w.instances.size : 1;
        const countLabel = instCount > 1
            ? getDashFriendCountLabel(w.friends.length, 'dashboard.worlds.count_world')
            : getDashFriendCountLabel(w.friends.length, 'dashboard.worlds.count_here');
        const { cls: dwInstCls, label: dwInstLabel } = getInstanceBadge(w.instanceType);
        return `<div class="vrcn-content-card" onclick="openWorldDetail('${wid}')">
            <div class="cc-bg" style="${thumbStyle}"></div>
            <div class="cc-scrim"></div>
            <div class="cc-badges-top"><span class="vrcn-badge ${dwInstCls}">${dwInstLabel}</span></div>
            <div class="cc-content">
                <div class="cc-name">${esc(displayName)}</div>
                <div class="cc-friends-row">${friendAvatars}${extra}</div>
                <div class="cc-bottom-row">
                    <div class="cc-meta"><span class="msi">person</span>${countLabel}</div>
                    ${instCount > 1 ? `<span style="font-size:9px;color:rgba(255,255,255,.4);">${tf('dashboard.worlds.instances', { count: instCount }, '{count} instances')}</span>` : ''}
                </div>
            </div>
        </div>`;
    }).join('');
}

function renderDashFriendsFeed() {
    const el = document.getElementById('dashFriendsFeed');
    if (!currentVrcUser || !vrcFriendsLoaded) {
        el.innerHTML = sk('feed', 8);
        return;
    }
    if (!vrcFriendsData.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.friends.empty', 'No friends online')}</div>`;
        return;
    }
    const activeFriends = vrcFriendsData.filter(f => f.presence !== 'offline');
    el.innerHTML = activeFriends.slice(0, 12).map(f => {
        const img = f.image || '';
        const imgTag = img
            ? `<img class="dash-feed-avatar" src="${img}" onerror="this.style.display='none'">`
            : `<div class="dash-feed-avatar" style="display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:700;color:var(--tx3)">${esc((f.displayName||'?')[0])}</div>`;
        const { worldId } = parseFriendLocation(f.location);
        const cached = worldId ? dashWorldCache[worldId] : null;
        const isPrivate = !f.location || f.location === 'private';
        const loc = f.presence === 'web'
            ? t('dashboard.friends.location_web', 'Web / Mobile')
            : (isPrivate ? t('dashboard.friends.location_private', 'Private Instance') : (cached?.name || t('dashboard.friends.location_world', 'In World')));
        const fid = (f.id || '').replace(/'/g, "\\'");
        const dotClass = f.presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        return `<div class="dash-feed-card" onclick="openFriendDetail('${fid}')">
            ${imgTag}
            <div class="dash-feed-info">
                <div class="dash-feed-name"><span class="${dotClass} ${statusDotClass(f.status)}" style="width:7px;height:7px;"></span>${esc(f.displayName)}</div>
                <div class="dash-feed-status">${esc(f.statusDescription || statusLabel(f.status))}</div>
                <div class="dash-feed-loc">${esc(loc)}</div>
            </div>
        </div>`;
    }).join('');
}

function browseDashBg() {
    sendToCS({ action: 'browseDashBg' });
}

/* === My Instances === */
let _myInstancesData = [];

function loadMyInstances() {
    sendToCS({ action: 'vrcGetMyInstances' });
}

function refreshMyInstances() {
    const btn = document.getElementById('miRefreshBtn');
    if (btn) btn.classList.add('spinning');
    sendToCS({ action: 'vrcGetMyInstances' });
    // Spinner stops when renderMyInstances is called (response arrives)
}

function renderMyInstances(instances) {
    _myInstancesData = instances || [];
    const label = document.getElementById('dashMyInstancesLabel');
    const grid  = document.getElementById('dashMyInstances');
    const btn   = document.getElementById('miRefreshBtn');
    if (btn) btn.classList.remove('spinning');
    if (!label || !grid) return;

    // CSS class controls wrap visibility; data-hidden (layout) takes priority via !important
    const wrap = label.closest('.dash-section-wrap');
    if (wrap) wrap.classList.toggle('mi-has-instances', !!_myInstancesData.length);

    if (!_myInstancesData.length) {
        label.style.display = 'none';
        grid.style.display  = 'none';
        return;
    }
    // If layout has hidden this section, don't render content
    if (wrap?.hasAttribute('data-hidden')) return;

    label.style.display = '';
    grid.style.display  = '';

    grid.innerHTML = _myInstancesData.map(inst => {
        const { cls, label: typeLabel } = getInstanceBadge(inst.instanceType);
        const thumbStyle = inst.worldThumb ? `background-image:url('${cssUrl(inst.worldThumb)}')` : '';
        const wid = (inst.worldId || '').replace(/'/g, "\\'");
        const count = inst.userCount || 0;
        const cap   = inst.capacity  || 0;
        const countStr = cap > 0
            ? tf('dashboard.instances.players_with_capacity', { count, capacity: cap }, '{count}/{capacity} players')
            : tf('dashboard.instances.players', { count }, '{count} players');
        const safeLoc = (inst.location || '').replace(/'/g, "\\'");
        return `<div class="vrcn-content-card" onclick="openMyInstanceDetail('${wid}','${safeLoc}')" data-location="${esc(inst.location || '')}">
            <div class="cc-bg" style="${thumbStyle}"></div>
            <div class="cc-scrim"></div>
            <div class="cc-badges-top"><span class="vrcn-badge ${cls}">${esc(typeLabel)}</span></div>
            <div class="cc-content">
                <div class="cc-name">${esc(inst.worldName || inst.worldId || t('dashboard.instances.unknown_world', 'Unknown World'))}</div>
                <div class="cc-bottom-row">
                    <div class="cc-meta"><span class="msi">person</span>${esc(countStr)}</div>
                </div>
            </div>
        </div>`;
    }).join('');
}

function removeMyInstance(location) {
    sendToCS({ action: 'vrcRemoveMyInstance', location });
    _myInstancesData = _myInstancesData.filter(i => i.location !== location);
    renderMyInstances(_myInstancesData);
    closeMyInstanceDetail();
    showToast(true, t('dashboard.instances.removed', 'Instance removed.'));
}

function closeMyInstanceDetail() {
    document.getElementById('modalMyInstance').style.display = 'none';
}

function openMyInstanceDetail(worldId, location) {
    const inst = _myInstancesData.find(i => i.location === location) || _myInstancesData.find(i => i.worldId === worldId);
    if (!inst) return;

    const m  = document.getElementById('modalMyInstance');
    const c  = document.getElementById('myInstanceContent');
    const thumb = inst.worldThumb || '';
    const { cls, label: typeLabel } = getInstanceBadge(inst.instanceType);
    const instNum = (inst.location || '').match(/:(\d+)/)?.[1] || '';
    const canJoin = inst.instanceType !== 'private' && inst.instanceType !== 'invite_plus';

    // Friends in this instance (match by instance number)
    const instFriends = (typeof vrcFriendsData !== 'undefined')
        ? vrcFriendsData.filter(f => f.location && f.location.match(/:(\d+)/)?.[1] === instNum)
        : [];

    const bannerHtml = thumb
        ? `<div class="fd-banner"><img src="${thumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';

    const copyBadge = instNum
        ? `<span class="vrcn-id-clip" onclick="copyInstanceLink('${jsq(inst.location)}')"><span class="msi" style="font-size:12px;">content_copy</span>#${esc(instNum)}</span>`
        : '';

    let friendsHtml = `<div class="wd-friends-label">${tf('dashboard.instances.friends_title', { count: instFriends.length }, 'FRIENDS IN THIS INSTANCE ({count})')}</div><div class="wd-friends-list">`;
    if (instFriends.length > 0) {
        instFriends.forEach(f => {
            friendsHtml += renderProfileItem(f, `closeMyInstanceDetail();openFriendDetail('${jsq(f.id || '')}')`);
        });
    } else {
        friendsHtml += `<div class="vrcn-profile-item" style="pointer-events:none;opacity:0.55;">
            <div class="fd-profile-item-avatar" style="display:flex;align-items:center;justify-content:center;"><span class="msi" style="font-size:20px;color:var(--tx3);">person</span></div>
            <div class="fd-profile-item-info">
                <div class="fd-profile-item-name">${t('dashboard.instances.no_friends_title', 'No friends here yet!')}</div>
                <div class="fd-profile-item-status">${t('dashboard.instances.no_friends_desc', 'Invite friends to this instance!')}</div>
            </div>
        </div>`;
    }
    friendsHtml += '</div>';

    const mloc = jsq(inst.location || '');
    const mwn  = jsq(inst.worldName || '');
    const mwt  = jsq(inst.worldThumb || '');
    const mit  = jsq(inst.instanceType || '');
    const loc  = (inst.location || '').replace(/'/g, "\\'");

    c.innerHTML = `${bannerHtml}<div class="fd-content${thumb ? ' fd-has-banner' : ''}" style="padding:16px;">
        <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(inst.worldName || inst.worldId || t('dashboard.instances.unknown_world', 'Unknown World'))}</h2>
        <div style="display:flex;justify-content:flex-end;gap:6px;margin-bottom:4px;">
            <button class="vrcn-button-round" title="${esc(t('dashboard.instances.invite_friends', 'Invite Friends'))}" onclick="closeMyInstanceDetail();openInviteModalForLocation('${mloc}','${mwn}','${mwt}','${mit}')"><span class="msi" style="font-size:16px;">person_add</span></button>
            <button class="vrcn-button-round vrcn-btn-danger" title="${esc(t('dashboard.instances.remove_instance', 'Remove Instance'))}" onclick="removeMyInstance('${loc}')"><span class="msi" style="font-size:16px;">delete</span></button>
        </div>
        <div class="fd-badges-row"><span class="vrcn-badge ${cls}">${typeLabel}</span>${getOwnerBadgeHtml(inst.ownerId, inst.ownerName, inst.ownerGroup, 'closeMyInstanceDetail()')}${copyBadge}</div>
        ${friendsHtml}
        <div class="fd-actions">
            <button class="vrcn-button-round vrcn-btn-join" onclick="closeMyInstanceDetail();sendToCS({action:'vrcJoinFriend',location:'${loc}'})">${t('dashboard.instances.join_world', 'Join World')}</button>
            <button class="vrcn-button-round" onclick="closeMyInstanceDetail();openWorldSearchDetail('${jsq(worldId)}')">${t('dashboard.instances.open_world', 'Open World')}</button>
            <button class="vrcn-button-round" style="margin-left:auto;" onclick="closeMyInstanceDetail()">${t('common.close', 'Close')}</button>
        </div>
    </div>`;
    m.style.display = 'flex';
}

/* === Discovery Section === */
let _discTab  = 'popular';
let _discPage = 0;
const DISC_PER_PAGE   = 8;
const DISC_CACHE_TTL  = 10 * 60 * 1000;
let _popularCache = { worlds: [], ts: 0 };
let _activeCache  = { worlds: [], ts: 0 };
let _recentCache  = { worlds: [], ts: 0 };

// Refresh every 10 minutes (initial fetch triggered by vrcUser login event)
setInterval(() => {
    sendToCS({ action: 'vrcGetPopularWorlds' });
    sendToCS({ action: 'vrcGetActiveWorlds' });
}, DISC_CACHE_TTL);

function fetchWorldTabs() {
    sendToCS({ action: 'vrcGetPopularWorlds' });
    sendToCS({ action: 'vrcGetActiveWorlds' });
}

function setDiscoveryTab(tab) {
    _discTab  = tab;
    _discPage = 0;
    document.querySelectorAll('.disc-tab').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.tab === tab);
    });
    if (tab === 'popular') _fetchPopularWorlds();
    else if (tab === 'active') _fetchActiveWorlds();
    else if (tab === 'recent') _fetchRecentWorlds();
}

function _fetchPopularWorlds() {
    if (Date.now() - _popularCache.ts < DISC_CACHE_TTL && _popularCache.worlds.length) {
        renderDiscoverySection();
        return;
    }
    document.getElementById('dashDiscoveryGrid').innerHTML = `<div class="empty-msg">${t('dashboard.discovery.loading', 'Loading worlds...')}</div>`;
    sendToCS({ action: 'vrcGetPopularWorlds' });
}

function _fetchActiveWorlds() {
    if (Date.now() - _activeCache.ts < DISC_CACHE_TTL && _activeCache.worlds.length) {
        renderDiscoverySection();
        return;
    }
    document.getElementById('dashDiscoveryGrid').innerHTML = `<div class="empty-msg">${t('dashboard.discovery.loading', 'Loading worlds...')}</div>`;
    sendToCS({ action: 'vrcGetActiveWorlds' });
}

function _fetchRecentWorlds() {
    if (Date.now() - _recentCache.ts < DISC_CACHE_TTL && _recentCache.worlds.length) {
        renderDiscoverySection();
        return;
    }
    document.getElementById('dashDiscoveryGrid').innerHTML = `<div class="empty-msg">${t('dashboard.discovery.loading', 'Loading worlds...')}</div>`;
    sendToCS({ action: 'vrcGetRecentWorlds' });
}

function onRecentWorlds(worlds) {
    _recentCache = { worlds: worlds || [], ts: Date.now() };
    if (_discTab === 'recent') renderDiscoverySection();
    renderDashRecentlyVisited();
}

function onPopularWorlds(worlds) {
    _popularCache = { worlds: worlds || [], ts: Date.now() };
    if (_discTab === 'popular') renderDiscoverySection();
    renderDashPopularWorlds();
}

function onActiveWorlds(worlds) {
    _activeCache = { worlds: worlds || [], ts: Date.now() };
    if (_discTab === 'active') renderDiscoverySection();
    renderDashActiveWorlds();
}

function discPageChange(dir) {
    const cache = _discTab === 'popular' ? _popularCache : _discTab === 'active' ? _activeCache : _recentCache;
    const maxPage = Math.max(0, Math.ceil(cache.worlds.length / DISC_PER_PAGE) - 1);
    _discPage = Math.max(0, Math.min(maxPage, _discPage + dir));
    renderDiscoverySection();
}

function renderDiscoverySection() {
    const grid       = document.getElementById('dashDiscoveryGrid');
    const pagination = document.getElementById('discPagination');
    if (!grid) return;

    const cache = _discTab === 'popular' ? _popularCache : _discTab === 'active' ? _activeCache : _recentCache;
    if (!cache.worlds.length) {
        grid.innerHTML = `<div class="empty-msg">${t('dashboard.discovery.loading', 'Loading worlds...')}</div>`;
        if (pagination) pagination.style.display = 'none';
        return;
    }

    const totalPages = Math.max(1, Math.ceil(cache.worlds.length / DISC_PER_PAGE));
    _discPage = Math.min(_discPage, totalPages - 1);
    const page = cache.worlds.slice(_discPage * DISC_PER_PAGE, (_discPage + 1) * DISC_PER_PAGE);

    grid.innerHTML = page.map(w => {
        const name  = esc(w.name || w.id || '');
        const thumb = w.thumbnailImageUrl || w.imageUrl || '';
        const thumbStyle = thumb ? `background-image:url('${cssUrl(thumb)}')` : '';
        const wid = (w.id || '').replace(/'/g, "\\'");
        const occupants = w.occupants ?? w.publicOccupants ?? 0;
        const playingStr = occupants > 0
            ? tf('dashboard.discovery.playing', { count: occupants.toLocaleString() }, '{count} playing')
            : '';
        return `<div class="vrcn-content-card" onclick="openWorldSearchDetail('${wid}')">
            <div class="cc-bg" style="${thumbStyle}"></div>
            <div class="cc-scrim"></div>
            <div class="cc-content">
                <div class="cc-name">${name}</div>
                ${playingStr ? `<div class="cc-bottom-row"><div class="cc-meta"><span class="msi">person</span>${playingStr}</div></div>` : ''}
            </div>
        </div>`;
    }).join('');

    if (pagination) {
        pagination.style.display = totalPages > 1 ? 'flex' : 'none';
        const lbl = document.getElementById('discPageLabel');
        if (lbl) lbl.textContent = `${_discPage + 1} / ${totalPages}`;
        const prev = document.getElementById('discPrevBtn');
        const next = document.getElementById('discNextBtn');
        if (prev) prev.disabled = _discPage === 0;
        if (next) next.disabled = _discPage >= totalPages - 1;
    }
}

/* === Dashboard — Favorite Worlds shelf === */

function renderDashFavWorlds() {
    const el = document.getElementById('dashFavWorldsShelf');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.favworlds.login', 'Login to see favorite worlds')}</div>`;
        return;
    }
    const worlds = (typeof favWorldsData !== 'undefined') ? favWorldsData : [];
    const loaded = (typeof _favWorldsLoaded !== 'undefined') ? _favWorldsLoaded : false;
    if (!worlds.length && !loaded) {
        el.innerHTML = _dashWorldShelfSkeleton();
        sendToCS({ action: 'vrcGetFavoriteWorlds' });
        return;
    }
    if (!worlds.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.favworlds.empty', 'No favorite worlds yet')}</div>`;
        return;
    }
    el.innerHTML = worlds.slice(0, 20).map(_dashWorldCard).join('');
}

/* === Dashboard — World shelves (Recently Visited / Popular / Active) === */

function _dashWorldCard(w) {
    const thumb     = w.thumbnailImageUrl || w.imageUrl || '';
    const wid       = jsq(w.id || '');
    const occupants = w.occupants ?? w.publicOccupants ?? 0;
    const meta      = occupants > 0
        ? `<div class="cc-bottom-row"><div class="cc-meta"><span class="msi">person</span>${occupants.toLocaleString()}</div></div>`
        : '';
    return `<div class="vrcn-content-card" onclick="openWorldSearchDetail('${wid}')">
        <div class="cc-bg"${thumb ? ` style="background-image:url('${cssUrl(thumb)}')"` : ''}></div>
        <div class="cc-scrim"></div>
        <div class="cc-content"><div class="cc-name">${esc(w.name || w.id || '?')}</div>${meta}</div>
    </div>`;
}

function _dashWorldShelfSkeleton() {
    return Array.from({ length: 8 }, () => `<div class="vrcn-content-card sk-block" style="pointer-events:none;"></div>`).join('');
}

function renderDashRecentlyVisited() {
    const el = document.getElementById('dashRecentlyVisitedShelf');
    if (!el) return;
    if (!currentVrcUser) { el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.login','Login to see worlds')}</div>`; return; }
    const worlds = _recentCache.worlds;
    if (!worlds.length) { el.innerHTML = _dashWorldShelfSkeleton(); sendToCS({ action: 'vrcGetRecentWorlds' }); return; }
    el.innerHTML = worlds.slice(0, 20).map(_dashWorldCard).join('');
}

function renderDashPopularWorlds() {
    const el = document.getElementById('dashPopularWorldsShelf');
    if (!el) return;
    if (!currentVrcUser) { el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.login','Login to see worlds')}</div>`; return; }
    const worlds = _popularCache.worlds;
    if (!worlds.length) { el.innerHTML = _dashWorldShelfSkeleton(); sendToCS({ action: 'vrcGetPopularWorlds' }); return; }
    el.innerHTML = worlds.slice(0, 20).map(_dashWorldCard).join('');
}

function renderDashActiveWorlds() {
    const el = document.getElementById('dashActiveWorldsShelf');
    if (!el) return;
    if (!currentVrcUser) { el.innerHTML = `<div class="empty-msg">${t('dashboard.worlds.login','Login to see worlds')}</div>`; return; }
    const worlds = _activeCache.worlds;
    if (!worlds.length) { el.innerHTML = _dashWorldShelfSkeleton(); sendToCS({ action: 'vrcGetActiveWorlds' }); return; }
    el.innerHTML = worlds.slice(0, 20).map(_dashWorldCard).join('');
}

/* === Dashboard — Group Activity grid === */

function _dashGroupSkeleton() {
    const card = `<div class="dash-group-card" style="pointer-events:none;">
        <div class="dash-group-icon sk-block"></div>
        <div class="dash-group-info">
            <div class="sk-block" style="height:11px;width:70%;border-radius:4px;margin-bottom:5px;"></div>
            <div class="sk-block" style="height:9px;width:45%;border-radius:4px;"></div>
        </div>
    </div>`;
    return Array.from({ length: 4 }, () => card).join('');
}

function renderDashGroupActivity() {
    const el = document.getElementById('dashGroupActivityGrid');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.groups.login', 'Login to see your groups')}</div>`;
        return;
    }
    const groups = (typeof myGroups !== 'undefined') ? myGroups : [];
    const loaded = (typeof myGroupsLoaded !== 'undefined') ? myGroupsLoaded : false;
    if (!groups.length && !loaded) {
        el.innerHTML = _dashGroupSkeleton();
        sendToCS({ action: 'vrcGetMyGroups' });
        return;
    }
    if (!groups.length) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.groups.empty', 'No groups joined yet')}</div>`;
        return;
    }
    el.innerHTML = groups.slice(0, 8).map(g => {
        const gid  = jsq(g.id || '');
        const icon = g.iconUrl || '';
        const cnt  = g.memberCount || 0;
        const iconHtml = icon
            ? `<img src="${icon}" onerror="this.parentElement.innerHTML='<span class=msi>group</span>'">`
            : `<span class="msi">group</span>`;
        const metaHtml = cnt > 0
            ? `<span class="msi">person</span>${esc(cnt.toLocaleString())}`
            : (g.shortCode ? esc(g.shortCode) : '');
        return `<div class="dash-group-card" onclick="openGroupDetail('${gid}')">
            <div class="dash-group-icon">${iconHtml}</div>
            <div class="dash-group-info">
                <div class="dash-group-name">${esc(g.name || '?')}</div>
                <div class="dash-group-meta">${metaHtml}</div>
            </div>
        </div>`;
    }).join('');
}

/* === Dashboard — Recent Activity Timeline === */

function _dashTlSkeleton(n = 5) {
    const row = `<tr style="pointer-events:none;">
        <td><span class="sk-block" style="height:9px;width:80px;border-radius:3px;display:inline-block;"></span></td>
        <td><span class="sk-block" style="height:9px;width:70px;border-radius:3px;display:inline-block;"></span></td>
        <td><span class="sk-block" style="height:9px;width:50px;border-radius:3px;display:inline-block;"></span></td>
        <td><span class="sk-block" style="height:9px;width:100px;border-radius:3px;display:inline-block;"></span></td>
    </tr>`;
    return `<div class="tl-list-wrap"><table class="tl-list-table"><tbody>${Array.from({ length: n }, () => row).join('')}</tbody></table></div>`;
}

function _dashTlRelative(ts) {
    const diff = Date.now() - new Date(ts).getTime();
    const m = Math.floor(diff / 60000);
    if (m < 1)  return t('common.time.just_now', 'just now');
    if (m < 60) return tf('common.time.min_ago',  { m }, '{m}m ago');
    const h = Math.floor(m / 60);
    if (h < 24) return tf('common.time.hour_ago', { h }, '{h}h ago');
    const d = Math.floor(h / 24);
    return tf('common.time.day_ago', { d }, '{d}d ago');
}

function _dashTlDetail(ev, isFriend) {
    if (isFriend) {
        switch (ev.type) {
            case 'friend_gps':        return ev.worldName || ev.worldId || '';
            case 'friend_status':     return ev.newValue || '';
            case 'friend_statusdesc': return ev.newValue || '';
            case 'friend_bio':        return ev.newValue || '';
            case 'friend_online':
            case 'friend_offline':
            case 'friend_added':
            case 'friend_removed':    return '';
            default:                  return '';
        }
    }
    switch (ev.type) {
        case 'instance_join':  return ev.worldName || ev.worldId || '';
        case 'photo':          return ev.worldName || (ev.photoPath ? ev.photoPath.split(/[\\/]/).pop() : '') || '';
        case 'first_meet':
        case 'meet_again':     return ev.userName || '';
        case 'avatar_switch':  return ev.avatarName || '';
        case 'notification':   return ev.notifType || '';
        case 'video_url':      return ev.url || '';
        default:               return '';
    }
}

function _dashTlRows(events, isFriend) {
    if (!events.length) {
        return `<div class="empty-msg" style="padding:16px 8px;">${t('dashboard.timeline.empty', 'No events yet')}</div>`;
    }
    const sliced = events.slice(0, 8);
    if (isFriend) {
        return typeof buildFriendListHtml === 'function'
            ? buildFriendListHtml(sliced)
            : `<div class="empty-msg">${t('dashboard.timeline.empty', 'No events yet')}</div>`;
    }
    return typeof buildPersonalListHtml === 'function'
        ? buildPersonalListHtml(sliced)
        : `<div class="empty-msg">${t('dashboard.timeline.empty', 'No events yet')}</div>`;
}

function renderDashMyRecentTimeline() {
    const el = document.getElementById('dashMyRecentTl');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.timeline.login', 'Login to see recent activity')}</div>`;
        return;
    }
    const personal = (typeof timelineEvents !== 'undefined') ? timelineEvents : [];
    if (!personal.length) sendToCS({ action: 'vrcGetTimeline', offset: 0 });
    el.innerHTML = personal.length ? _dashTlRows(personal, false) : _dashTlSkeleton();
}

function renderDashFriendsRecentTimeline() {
    const el = document.getElementById('dashFriendsRecentTl');
    if (!el) return;
    if (!currentVrcUser) {
        el.innerHTML = `<div class="empty-msg">${t('dashboard.timeline.login', 'Login to see recent activity')}</div>`;
        return;
    }
    const friends = (typeof friendTimelineEvents !== 'undefined') ? friendTimelineEvents : [];
    if (!friends.length) sendToCS({ action: 'getFriendTimeline', type: '' });
    el.innerHTML = friends.length ? _dashTlRows(friends, true) : _dashTlSkeleton();
}

function renderDashRecentTimeline() {
    renderDashMyRecentTimeline();
    renderDashFriendsRecentTimeline();
}

/* === Dashboard Layout System === */

const DASH_SECTION_META = [
    { id: 'my_instances',            nameKey: 'dashboard.section.my_instances',            name: 'Your Instances' },
    { id: 'friend_locations',        nameKey: 'dashboard.section.friend_locations',        name: 'Friends Locations' },
    { id: 'discovery',               nameKey: 'dashboard.section.discovery',               name: 'Discover Worlds' },
    { id: 'friend_activity',         nameKey: 'dashboard.section.friend_activity',         name: 'Friends Activity' },
    { id: 'fav_worlds',              nameKey: 'dashboard.section.fav_worlds',              name: 'Favorite Worlds' },
    { id: 'groups',                  nameKey: 'dashboard.section.your_groups',             name: 'Your Groups' },
    { id: 'recently_visited',        nameKey: 'dashboard.section.recently_visited',        name: 'Recently Visited' },
    { id: 'popular_worlds',          nameKey: 'dashboard.section.popular_worlds',          name: 'Popular Worlds' },
    { id: 'active_worlds',           nameKey: 'dashboard.section.active_worlds',           name: 'Very Active Worlds' },
    { id: 'my_recent_activity',      nameKey: 'dashboard.section.my_recent_activity',      name: 'My Recent Activity' },
    { id: 'friends_recent_activity', nameKey: 'dashboard.section.friends_recent_activity', name: 'Friends Recent Activity' },
];
const DASH_DEFAULT_ORDER   = DASH_SECTION_META.map(s => s.id);
const DASH_DEFAULT_VISIBLE = new Set(['my_instances', 'friend_locations', 'discovery', 'friend_activity']);

let _dashLayout = {
    order:  [...DASH_DEFAULT_ORDER],
    hidden: DASH_DEFAULT_ORDER.filter(id => !DASH_DEFAULT_VISIBLE.has(id)),
};
let _dashModalLayout = null;

function loadDashLayout(data) {
    if (!data) { applyDashLayout(); return; }
    const rawOrder  = data.order  ?? data.dashSectionOrder  ?? data.DashSectionOrder  ?? null;
    const rawHidden = data.hidden ?? data.dashSectionHidden ?? data.DashSectionHidden ?? null;
    if (Array.isArray(rawOrder) && rawOrder.length) {
        const known   = rawOrder.filter(id => DASH_DEFAULT_ORDER.includes(id));
        const missing = DASH_DEFAULT_ORDER.filter(id => !known.includes(id));
        _dashLayout.order = [...known, ...missing];
    }
    if (Array.isArray(rawHidden)) {
        _dashLayout.hidden = rawHidden.filter(id => DASH_DEFAULT_ORDER.includes(id));
    }
    applyDashLayout();
}

function applyDashLayout() {
    const container = document.getElementById('dashSectionsContainer');
    if (!container) return;
    // CSS flex order — no DOM moves, no scroll jumps
    _dashLayout.order.forEach((id, idx) => {
        const wrap = container.querySelector(`.dash-section-wrap[data-section="${id}"]`);
        if (!wrap) return;
        wrap.style.order = idx;
        const hidden = _dashLayout.hidden.includes(id);
        wrap.toggleAttribute('data-hidden', hidden);
        if (!hidden && id === 'my_instances') renderMyInstances(_myInstancesData);
    });
}

function openDashLayoutEditor() {
    _dashModalLayout = { order: [..._dashLayout.order], hidden: [..._dashLayout.hidden] };
    _renderDashLayoutList();
    document.getElementById('dashLayoutModal').style.display = 'flex';
}

function closeDashLayoutEditor() {
    document.getElementById('dashLayoutModal').style.display = 'none';
    _dashModalLayout = null;
}

function _renderDashLayoutList() {
    const list = document.getElementById('dashLayoutList');
    if (!list || !_dashModalLayout) return;
    list.innerHTML = _dashModalLayout.order.map((id) => {
        const meta   = DASH_SECTION_META.find(s => s.id === id) || { nameKey: id, name: id };
        const label  = t(meta.nameKey, meta.name);
        const hidden = _dashModalLayout.hidden.includes(id);
        const sid    = jsq(id);
        return `<div class="dash-layout-item${hidden ? ' dli-hidden' : ''}" data-id="${esc(id)}">
            <span class="msi dash-drag-handle" onmousedown="dashDragStart(event,'${sid}')">drag_indicator</span>
            <span class="dash-layout-name">${esc(label)}</span>
            <button class="vrcn-button-round" style="padding:4px 8px;" onclick="dashModalToggle('${sid}')">
                <span class="msi" style="font-size:16px;">${hidden ? 'visibility_off' : 'visibility'}</span>
            </button>
        </div>`;
    }).join('');
}

function dashDragStart(e, id) {
    e.preventDefault();
    const list = document.getElementById('dashLayoutList');
    if (!list || !_dashModalLayout) return;
    const dragEl = list.querySelector(`.dash-layout-item[data-id="${CSS.escape(id)}"]`);
    if (!dragEl) return;

    dragEl.classList.add('dli-dragging');
    document.body.style.cursor = 'grabbing';

    function onMove(ev) {
        // Temporarily hide the dragged element so elementFromPoint finds the item underneath
        dragEl.style.pointerEvents = 'none';
        const under = document.elementFromPoint(ev.clientX, ev.clientY);
        dragEl.style.pointerEvents = '';
        const target = under?.closest('#dashLayoutList .dash-layout-item[data-id]');
        if (!target || target === dragEl) return;

        const rect   = target.getBoundingClientRect();
        const middle = rect.top + rect.height / 2;
        const insertBefore = ev.clientY < middle;

        // FLIP — snapshot positions of all non-dragged items before the move
        const snapshots = new Map();
        list.querySelectorAll('.dash-layout-item[data-id]').forEach(el => {
            if (el !== dragEl) snapshots.set(el, el.getBoundingClientRect().top);
        });

        if (insertBefore) {
            list.insertBefore(dragEl, target);
        } else {
            list.insertBefore(dragEl, target.nextSibling);
        }

        // FLIP — animate items that moved due to the insert
        list.querySelectorAll('.dash-layout-item[data-id]').forEach(el => {
            if (el === dragEl || !snapshots.has(el)) return;
            const delta = snapshots.get(el) - el.getBoundingClientRect().top;
            if (!delta) return;
            el.style.transition = 'none';
            el.style.transform  = `translateY(${delta}px)`;
        });
        list.offsetHeight; // force reflow
        list.querySelectorAll('.dash-layout-item[data-id]').forEach(el => {
            if (el === dragEl || !el.style.transform) return;
            el.style.transition = 'transform 0.15s ease';
            el.style.transform  = '';
        });
    }

    function onUp() {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup',   onUp);
        dragEl.classList.remove('dli-dragging');
        document.body.style.cursor = '';
        // Sync _dashModalLayout.order from current DOM order
        _dashModalLayout.order = [...list.querySelectorAll('.dash-layout-item[data-id]')]
            .map(el => el.dataset.id);
    }

    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup',   onUp);
}

function dashModalToggle(id) {
    if (!_dashModalLayout) return;
    const i = _dashModalLayout.hidden.indexOf(id);
    if (i === -1) _dashModalLayout.hidden.push(id);
    else _dashModalLayout.hidden.splice(i, 1);
    _renderDashLayoutList();
}

function dashLayoutReset() {
    if (!_dashModalLayout) return;
    _dashModalLayout.order  = [...DASH_DEFAULT_ORDER];
    _dashModalLayout.hidden = DASH_DEFAULT_ORDER.filter(id => !DASH_DEFAULT_VISIBLE.has(id));
    _renderDashLayoutList();
}

function saveDashLayoutFromModal() {
    if (!_dashModalLayout) return;
    _dashLayout = { order: [..._dashModalLayout.order], hidden: [..._dashModalLayout.hidden] };
    closeDashLayoutEditor();
    applyDashLayout();
    saveSettings();
}

// Drag-to-scroll on shelves
(function () {
    let _shelf = null, _startX = 0, _scrollStart = 0, _dragging = false;

    document.addEventListener('mousedown', e => {
        const shelf = e.target.closest('.vrcn-dash-fav-shelf');
        if (!shelf) return;
        _shelf       = shelf;
        _startX      = e.clientX;
        _scrollStart = shelf.scrollLeft;
        _dragging    = false;
        shelf.style.cursor = 'grabbing';
        e.preventDefault();
    });

    document.addEventListener('mousemove', e => {
        if (!_shelf) return;
        const dx = e.clientX - _startX;
        if (!_dragging && Math.abs(dx) > 4) _dragging = true;
        if (_dragging) _shelf.scrollLeft = _scrollStart - dx;
    });

    document.addEventListener('mouseup', () => {
        if (!_shelf) return;
        _shelf.style.cursor = '';
        if (_dragging) {
            _shelf.addEventListener('click', ev => ev.stopPropagation(), { capture: true, once: true });
        }
        _shelf = null;
        _dragging = false;
    });
})();

function rerenderDashTranslations() {
    renderDashWorlds();
    renderDashFriendsFeed();
    renderDashFavWorlds();
    renderDashRecentlyVisited();
    renderDashPopularWorlds();
    renderDashActiveWorlds();
    renderDashGroupActivity();
    renderDashMyRecentTimeline();
    renderDashFriendsRecentTimeline();
    if (_dashModalLayout) _renderDashLayoutList();
}
document.documentElement.addEventListener('languagechange', rerenderDashTranslations);

