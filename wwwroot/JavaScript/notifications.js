/* === Notifications === */
let _notifDismiss = null;

function toggleNotifPanel() {
    notifPanelOpen = !notifPanelOpen;
    const panel = document.getElementById('notifPanel');
    if (notifPanelOpen) {
        panel.style.display = '';
        requestAnimationFrame(() => panel.classList.add('panel-open'));
        refreshNotifications();
        setTimeout(() => {
            _notifDismiss = e => {
                const panel = document.getElementById('notifPanel');
                const btn   = document.getElementById('btnNotif');
                // Use composedPath() so the check survives DOM mutations caused by
                // acceptNotif/declineNotif re-rendering the list before bubbling completes
                const path = e.composedPath();
                if (!path.includes(panel) && !path.includes(btn)) toggleNotifPanel();
            };
            document.addEventListener('click', _notifDismiss);
        }, 0);
    } else {
        if (_notifDismiss) { document.removeEventListener('click', _notifDismiss); _notifDismiss = null; }
        panel.classList.remove('panel-open');
        setTimeout(() => { if (!notifPanelOpen) panel.style.display = 'none'; }, 90);
    }
}

function refreshNotifications() {
    sendToCS({ action: 'vrcGetNotifications' });
}

function getNotificationTypeMeta(type) {
    switch (type) {
        case 'friendRequest': return { icon: 'person_add', label: t('notifications.types.friend_request', 'Friend Request') };
        case 'invite': return { icon: 'mail', label: t('notifications.types.world_invite', 'World Invite') };
        case 'requestInvite': return { icon: 'forward_to_inbox', label: t('notifications.types.invite_request', 'Invite Request') };
        case 'inviteResponse': return { icon: 'reply', label: t('notifications.types.invite_response', 'Invite Response') };
        case 'requestInviteResponse': return { icon: 'reply_all', label: t('notifications.types.invite_request_response', 'Invite Req. Response') };
        case 'votetokick': return { icon: 'gavel', label: t('notifications.types.vote_to_kick', 'Vote to Kick') };
        case 'boop': return { icon: 'waving_hand', label: t('notifications.types.boop', 'Boop') };
        case 'message': return { icon: 'chat', label: t('notifications.types.message', 'Message') };
        case 'group.announcement': return { icon: 'campaign', label: t('notifications.types.group_announcement', 'Group Announcement') };
        case 'group.invite': return { icon: 'group_add', label: t('notifications.types.group_invite', 'Group Invite') };
        case 'group.joinRequest': return { icon: 'group', label: t('notifications.types.group_join_request', 'Group Join Request') };
        case 'group.informationRequest': return { icon: 'info', label: t('notifications.types.group_info_request', 'Group Info Request') };
        case 'group.transfer': return { icon: 'swap_horiz', label: t('notifications.types.group_transfer', 'Group Transfer') };
        case 'group.informative': return { icon: 'info', label: t('notifications.types.group_info', 'Group Info') };
        case 'group.post': return { icon: 'article', label: t('notifications.types.group_post', 'Group Post') };
        case 'group.event.created': return { icon: 'event_note', label: t('notifications.types.group_event', 'Group Event') };
        case 'group.event.starting': return { icon: 'event_available', label: t('notifications.types.group_event_starting', 'Group Event Starting') };
        case 'avatarreview.success': return { icon: 'check_circle', label: t('notifications.types.avatar_approved', 'Avatar Approved') };
        case 'avatarreview.failure': return { icon: 'cancel', label: t('notifications.types.avatar_rejected', 'Avatar Rejected') };
        case 'badge.earned': return { icon: 'military_tech', label: t('notifications.types.badge_earned', 'Badge Earned') };
        case 'economy.alert': return { icon: 'account_balance_wallet', label: t('notifications.types.economy_alert', 'Economy Alert') };
        case 'economy.received.gift': return { icon: 'card_giftcard', label: t('notifications.types.gift_received', 'Gift Received') };
        case 'event.announcement': return { icon: 'event', label: t('notifications.types.event', 'Event') };
        case 'invite.instance.contentGated': return { icon: 'lock', label: t('notifications.types.content_gated_invite', 'Content Gated Invite') };
        case 'moderation.contentrestriction': return { icon: 'shield', label: t('notifications.types.content_restriction', 'Content Restriction') };
        case 'moderation.notice': return { icon: 'policy', label: t('notifications.types.moderation_notice', 'Moderation Notice') };
        case 'moderation.report.closed': return { icon: 'task_alt', label: t('notifications.types.report_closed', 'Report Closed') };
        case 'moderation.warning.group': return { icon: 'warning', label: t('notifications.types.group_warning', 'Group Warning') };
        case 'promo.redeem': return { icon: 'local_offer', label: t('notifications.types.promo_redeemed', 'Promo Redeemed') };
        case 'text.adventure': return { icon: 'auto_stories', label: t('notifications.types.text_adventure', 'Text Adventure') };
        case 'vrcplus.gift': return { icon: 'volunteer_activism', label: t('notifications.types.vrcplus_gift', 'VRC+ Gift') };
        default:
            if (type && type.startsWith('group.')) {
                return {
                    icon: 'groups',
                    label: tf('notifications.types.group_default', { name: type.replace('group.', '') }, 'Group: {name}')
                };
            }
            return { icon: 'notifications', label: type || t('common.notifications', 'Notifications') };
    }
}

function getNotificationTime(createdAt) {
    const locale = t('clock.date_locale', 'en-US');
    return createdAt ? new Date(createdAt).toLocaleString(locale) : '';
}

function formatInstanceTimer(joinedAt, now) {
    if (!joinedAt) return '';
    const seconds = Math.max(0, Math.floor((now - joinedAt) / 1000));
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    if (hours > 0) return tf('instance.timer.hours_minutes', { hours, minutes }, '{hours}h {minutes}m');
    if (minutes > 0) return tf('instance.timer.minutes', { minutes }, '{minutes}m');
    return t('instance.timer.less_than_minute', '<1m');
}

function _resolveNotifImage(n) {
    if (n._image) return n._image;
    if (n.senderUserId && typeof vrcFriendsData !== 'undefined') {
        const f = vrcFriendsData.find(f => f.id === n.senderUserId);
        if (f && f.image) return f.image;
    }
    return '';
}

function renderNotifications(list) {
    notifications = (list || []).filter(n => n.type !== 'boop'); // boops only in messenger
    const unseen = notifications.filter(n => !n.seen).length;
    const badge = document.getElementById('notifBadge');
    if (unseen > 0) { badge.textContent = unseen; badge.style.display = ''; }
    else badge.style.display = 'none';

    const el = document.getElementById('notifList');
    if (notifications.length === 0) {
        el.innerHTML = `<div class="empty-msg">${t('notifications.empty', 'No notifications')}</div>`;
        return;
    }
    el.innerHTML = notifications.map(n => {
        const { icon, label } = getNotificationTypeMeta(n.type);
        const time = getNotificationTime(n.created_at);
        const canAccept = ['friendRequest', 'invite', 'requestInvite', 'group.invite', 'group.joinRequest'].includes(n.type);
        const nid = esc(n.id);
        const senderLink = n.senderUserId
            ? `<strong style="cursor:pointer;" onclick="toggleNotifPanel();openFriendDetail('${esc(n.senderUserId)}')">${esc(n.senderUsername || n.senderUserId)}</strong>`
            : (n.senderUsername ? `<strong>${esc(n.senderUsername)}</strong>` : '');
        const det = typeof n.details === 'string' ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })() : (n.details || {});

        // Resolve avatar image
        const nImg = _resolveNotifImage(n);
        const hasImg = nImg && nImg.length > 5;
        const initial = (n.senderUsername || '?')[0].toUpperCase();
        const avatarHtml = hasImg
            ? `<div class="notif-avatar" style="background-image:url('${cssUrl(nImg)}')"><span class="msi notif-avatar-badge">${icon}</span></div>`
            : `<span class="msi notif-icon" style="font-size:18px;">${icon}</span>`;

        let titleHtml;
        let bodyHtml = '';
        if (n._v2 && n._title) {
            titleHtml = esc(n._title);
            if (n.message) bodyHtml = `<div class="notif-msg">${esc(n.message)}</div>`;
        } else if (n.type === 'invite') {
            const worldName = det.worldName ? esc(det.worldName) : t('notifications.unknown_world', 'unknown world');
            const wid = det.worldId ? det.worldId.split(':')[0] : '';
            const worldLink = wid
                ? `<strong style="cursor:pointer;" onclick="toggleNotifPanel();openWorldDetail('${esc(wid)}')">${worldName}</strong>`
                : `<strong>${worldName}</strong>`;
            const msg = det.inviteMessage || '';
            titleHtml = `${senderLink} <span style="color:var(--tx2);font-weight:400;">${t('notifications.title.invited_you_to', 'invited you to')}</span> ${worldLink}`;
            if (msg) bodyHtml = `<div class="notif-msg">${esc(msg)}</div>`;
        } else if (n.type === 'requestInvite') {
            const msg = det.requestMessage || '';
            titleHtml = `${senderLink} <span style="color:var(--tx2);font-weight:400;">${t('notifications.title.wants_invite', 'wants an invite')}</span>`;
            if (msg) bodyHtml = `<div class="notif-msg">${esc(msg)}</div>`;
        } else if (n.type === 'boop') {
            titleHtml = `${senderLink} <span style="color:var(--tx2);font-weight:400;">${t('notifications.title.booped_you', 'booped you')}</span>`;
        } else if (n.type === 'inviteResponse' || n.type === 'requestInviteResponse') {
            titleHtml = senderLink
                ? tf('notifications.title.from', { label: esc(label), sender: senderLink }, '{label} from {sender}')
                : esc(label);
            const msg = det.responseMessage || det.requestMessage || det.inviteMessage || n.message || '';
            if (msg) bodyHtml = `<div class="notif-msg">${esc(msg)}</div>`;
        } else {
            titleHtml = senderLink
                ? tf('notifications.title.from', { label: esc(label), sender: senderLink }, '{label} from {sender}')
                : esc(label);
            if (n.message) bodyHtml = `<div class="notif-msg">${esc(n.message)}</div>`;
        }
        return `<div class="notif-item ${n.seen && !canAccept ? 'notif-seen' : ''}" data-notif-id="${nid}">
            ${avatarHtml}
            <div class="notif-body">
                <div class="notif-title" style="display:flex;align-items:center;gap:5px;flex-wrap:nowrap;">${titleHtml}</div>
                ${bodyHtml}
                <div class="notif-time">${time}</div>
            </div>
            <div class="notif-actions">
                ${canAccept ? `<button class="vrcn-notify-button primary notif-accept-btn" onclick="acceptNotif('${nid}',this)"><span class="msi">check</span> ${t('notifications.actions.accept', 'Accept')}</button>` : ''}
                ${(canAccept || !n.seen) ? `<button class="vrcn-notify-button danger notif-decline-btn" onclick="declineNotif('${nid}',this)" title="${t('notifications.actions.decline', 'Decline')}"><span class="msi">close</span></button>` : ''}
            </div>
        </div>`;
    }).join('');
}

function acceptNotif(notifId, btn) {
    if (btn) { btn.disabled = true; btn.textContent = '...'; }
    const n = notifications.find(x => x.id === notifId);
    const det = typeof n?.details === 'string' ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })() : (n?.details || {});
    sendToCS({ action: 'vrcAcceptNotification', notifId, type: n?.type, details: det,
               _v2: n?._v2 || false, _data: n?._data || null, _link: n?._link || null,
               senderId: n?.senderUserId || null });
    // Remove immediately so the merge logic doesn't re-add it after REST refresh
    notifications = notifications.filter(x => x.id !== notifId);
    renderNotifications(notifications);
    setTimeout(() => refreshNotifications(), 1200);
}

function showLaunchModal(location, steamVrOpen) {
    closeLaunchModal();
    const el = document.createElement('div');
    el.className = 'modal-overlay';
    el.style.zIndex = '10003';
    el.innerHTML = `
        <div class="modal-box launch-modal">
            <div class="launch-modal-title">${t('notifications.launch.title', 'VRChat is not open')}</div>
            <div class="launch-modal-sub">${t('notifications.launch.subtitle', 'How do you want to play?')}</div>
            <div class="launch-modal-btns">
                <button class="vrcn-button${steamVrOpen ? ' vrcn-btn-primary' : ''}" onclick="launchAndJoin('${location}',true)">
                    <span class="msi">visibility</span> ${t('notifications.launch.play_vr', 'Play in VR')}
                </button>
                <button class="vrcn-button${!steamVrOpen ? ' vrcn-btn-primary' : ''}" onclick="launchAndJoin('${location}',false)">
                    <span class="msi">desktop_windows</span> ${t('notifications.launch.play_desktop', 'Play on Desktop')}
                </button>
            </div>
            <button class="launch-modal-cancel" onclick="closeLaunchModal()">${t('common.cancel', 'Cancel')}</button>
        </div>`;
    el.addEventListener('click', e => { if (e.target === el) closeLaunchModal(); });
    document.body.appendChild(el);
    window._launchModalEl = el;
}

function launchAndJoin(location, vr) {
    sendToCS({ action: 'vrcLaunchAndJoin', location, vr });
    closeLaunchModal();
}

function closeLaunchModal() {
    const el = window._launchModalEl;
    if (!el) return;
    el.style.opacity = '0';
    el.style.transition = 'opacity .15s';
    setTimeout(() => el.remove(), 150);
    window._launchModalEl = null;
}

function declineNotif(notifId, btn) {
    if (btn) { btn.disabled = true; btn.textContent = '...'; }
    const n = notifications.find(x => x.id === notifId);
    const det = typeof n?.details === 'string' ? (() => { try { return JSON.parse(n.details); } catch { return {}; } })() : (n?.details || {});
    sendToCS({ action: 'vrcHideNotification', notifId,
               type: n?.type, _v2: n?._v2 || false,
               details: det, _data: n?._data || null, _link: n?._link || null,
               senderId: n?.senderUserId || null });
    // Remove locally immediately
    notifications = notifications.filter(x => x.id !== notifId);
    setTimeout(() => renderNotifications(notifications), 300);
}


/* === Current Instance (sidebar) === */
function renderCurrentInstance(data) {
    currentInstanceData = data;

    // Feed Discord presence preview
    if (typeof dpOnInstanceUpdate === 'function' && data && !data.empty && !data.error && data.worldName) {
        const typeLabel = getInstanceBadge(data.instanceType).label;
        const shortId = (data.location || '').split(':')[1]?.split('~')[0] || '';
        const stateStr = `${typeLabel} #${shortId} (${data.nUsers}/${data.capacity})`;
        dpOnInstanceUpdate(data.worldName, data.worldThumb, stateStr, null);
    }

    const el = document.getElementById('vrcInstanceArea');
    if (!el) return;

    if (!data || data.empty) { el.innerHTML = ''; return; }
    if (data.error) {
        el.innerHTML = `<div style="font-size:11px;color:var(--err);padding:6px 0;">${esc(data.error)}</div>`;
        return;
    }
    if (!data.worldName && !data.worldId) { el.innerHTML = ''; return; }

    const name = data.worldName || data.worldId || t('instance.unknown_world', 'Unknown World');
    let users = data.users || [];

    // Build friend lookup maps
    const _byId = {}, _byName = {};
    vrcFriendsData.forEach(f => {
        if (f.id) _byId[f.id] = f;
        if (f.displayName) _byName[f.displayName.toLowerCase()] = f;
    });

    // If backend gave no users, fall back to friends in same location
    if (users.length === 0 && data.location && vrcFriendsData.length > 0) {
        const myLocBase = data.location.split('~')[0];
        users = vrcFriendsData.filter(f => {
            if (!f.location || f.location === 'private' || f.location === 'offline') return false;
            return f.location.split('~')[0] === myLocBase;
        });
    }

    // Enrich with friend images
    users = users.map(u => {
        if (u.image) return u;
        const m = (u.id && _byId[u.id]) || (u.displayName && _byName[(u.displayName || '').toLowerCase()]);
        return m ? { ...u, image: m.image || '', id: m.id || u.id } : u;
    });

    // Split: friends vs other players
    const friendUsers = users.filter(u =>
        (u.id && _byId[u.id]) || (u.displayName && _byName[(u.displayName || '').toLowerCase()]));
    const otherUsers = users.filter(u =>
        !(u.id && _byId[u.id]) && !(u.displayName && _byName[(u.displayName || '').toLowerCase()]));

    function renderSidebarRow(u, isFriend) {
        const hasImg = u.image && u.image.length > 5;
        const initial = (u.displayName || '?')[0].toUpperCase();
        const avatar = hasImg
            ? `<div class="inst-user-av" style="background-image:url('${cssUrl(u.image)}')"></div>`
            : `<div class="inst-user-av inst-user-av-letter">${esc(initial)}</div>`;
        const click = u.id ? ` onclick="openFriendDetail('${esc(u.id)}')"` : '';
        const statusLine = u.status
            ? `<span class="vrc-status-dot ${statusDotClass(u.status)}" style="width:6px;height:6px;flex-shrink:0;margin-right:3px;"></span><span class="inst-user-status">${esc(u.statusDescription || statusLabel(u.status))}</span>`
            : '';
        return `<div class="inst-user-row"${click}>${avatar}<div class="inst-user-info"><span class="inst-user-name">${esc(u.displayName)}</span>${statusLine ? `<div class="inst-user-status-row">${statusLine}</div>` : ''}</div></div>`;
    }

    const lbl = `font-size:10px;font-weight:700;color:var(--tx3);padding:6px 10px 2px;letter-spacing:.05em;`;
    let usersHtml = '';
    if (users.length > 0) {
        usersHtml = `<div class="inst-users">`;
        if (friendUsers.length > 0) {
            usersHtml += `<div style="${lbl}">${tf('instance.sections.friends_in_instance', { count: friendUsers.length }, 'FRIENDS IN INSTANCE ({count})')}</div>`;
            usersHtml += friendUsers.map(u => renderSidebarRow(u)).join('');
        }
        if (otherUsers.length > 0) {
            usersHtml += `<div style="${lbl}">${tf('instance.sections.players_in_instance', { count: otherUsers.length }, 'PLAYERS IN INSTANCE ({count})')}</div>`;
            usersHtml += otherUsers.map(u => renderSidebarRow(u)).join('');
        }
        usersHtml += `</div>`;
    } else {
        usersHtml = `<div style="font-size:11px;color:var(--tx3);padding:8px 10px;">${t('instance.no_player_data', 'No player data')}</div>`;
    }

    const { cls: _instCls, label: _instLabel } = getInstanceBadge(data.instanceType);
    const typeBadge = data.instanceType && data.instanceType !== 'public'
        ? `<span class="inst-type-badge vrcn-badge ${_instCls}">${esc(_instLabel)}</span>` : '';

    const displayCount = users.length || data.nUsers || 0;
    const prevInstScroll = el.querySelector('.inst-users')?.scrollTop || 0;
    el.innerHTML = `<div class="inst-card" data-inst-type="${_instCls}">
        <div class="inst-header" style="background-image:url('${cssUrl(data.worldThumb || '')}');cursor:pointer;" onclick="openInstanceInfoModal()">
            <div class="inst-header-fade"></div>
            ${typeBadge}
            <div class="inst-header-info">
                <div class="inst-world-name">${esc(name)}</div>
                <div class="inst-player-count"><span class="msi" style="font-size:13px;">person</span> ${displayCount}${data.capacity ? '/' + data.capacity : ''}</div>
            </div>
        </div>
        ${usersHtml}
        <div class="inst-invite-bar">
            <button class="vrcn-button inst-invite-btn" onclick="openInviteModal()">
                <span class="msi">person_add</span> ${t('dashboard.instances.invite_friends', 'Invite Friends')}
            </button>
        </div>
    </div>
    `;
    if (prevInstScroll > 0) {
        const newInstUsers = el.querySelector('.inst-users');
        if (newInstUsers) newInstUsers.scrollTop = prevInstScroll;
    }
    // If instance info modal is open, refresh it live
    const _iim = document.getElementById('modalInstanceInfo');
    if (_iim && _iim.style.display !== 'none') openInstanceInfoModal();
}

/* === Instance Info Modal === */

function openInstanceInfoModal() {
    const data = currentInstanceData;
    if (!data || data.empty || data.error || (!data.worldName && !data.worldId)) return;

    const m = document.getElementById('modalInstanceInfo');
    const c = document.getElementById('instanceInfoContent');
    if (!m || !c) return;

    const thumb = data.worldThumb || '';
    const name  = data.worldName || data.worldId || t('instance.unknown_world', 'Unknown World');
    const { cls: instCls, label: instLabel } = getInstanceBadge(data.instanceType);
    const instNum = (data.location || '').match(/:(\d+)/)?.[1] || '';

    const bannerHtml = thumb
        ? `<div class="fd-banner"><img src="${thumb}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div></div>`
        : '';

    // Build friend lookup maps
    const byId   = {};
    const byName = {};
    vrcFriendsData.forEach(f => {
        if (f.id)          byId[f.id] = f;
        if (f.displayName) byName[f.displayName.toLowerCase()] = f;
    });

    // User list — fall back to friends in same location
    let users = (data.users || []).slice();
    if (users.length === 0 && data.location) {
        const myLocBase = data.location.split('~')[0];
        users = vrcFriendsData.filter(f => {
            if (!f.location || f.location === 'private' || f.location === 'offline') return false;
            return f.location.split('~')[0] === myLocBase;
        });
    }

    // Enrich with live friend data
    const enriched = users.map(u => {
        const friend = (u.id && byId[u.id]) || (u.displayName && byName[(u.displayName || '').toLowerCase()]);
        return { ...u, _friend: friend || null };
    });

    // Sort oldest join first (longest at top)
    enriched.sort((a, b) => (a.joinedAt && b.joinedAt) ? a.joinedAt - b.joinedAt : 0);

    const now = Date.now();
    const hasTimers = enriched.some(u => u.joinedAt);

    function fmtTimer(joinedAt) {
        return formatInstanceTimer(joinedAt, now);
    }


    const timerTh = hasTimers ? `<th style="text-align:right;padding-right:10px;">${t('instance.table.timer', 'Timer')}</th>` : '';
    const thead = `<thead><tr>
        <th style="width:40px;">${t('instance.table.profile', 'Profile')}</th>
        ${timerTh}
        <th>${t('instance.table.display_name', 'Display Name')}</th>
        <th style="width:110px;">${t('instance.table.rank', 'Rank')}</th>
        <th>${t('instance.table.status', 'Status')}</th>
        <th style="width:46px;text-align:center;">18+</th>
        <th style="width:60px;text-align:center;">${t('instance.table.platform', 'Platform')}</th>
        <th style="min-width:70px;">${t('instance.table.language', 'Language')}</th>
    </tr></thead>`;

    const copyBadge = instNum
        ? `<span class="vrcn-id-clip" onclick="copyInstanceLink('${jsq(data.location || '')}')"><span class="msi" style="font-size:12px;">content_copy</span>#${esc(instNum)}</span>`
        : '';
    const wid = jsq(data.worldId || '');

    // Split enriched list into friends and non-friends
    const friendsEnriched = enriched.filter(u => !!u._friend);
    const othersEnriched  = enriched.filter(u => !u._friend);

    function makeRow(u) {
        const f           = u._friend;
        const id          = u.id || '';
        const displayName = u.displayName || '?';
        const image       = f?.image             || u.image             || '';
        const status      = f?.status            || u.status            || '';
        const statusDesc  = f?.statusDescription || u.statusDescription || '';
        const tags        = (f?.tags?.length ? f.tags : null) || u.tags || [];
        const platform    = f?.platform          || u.platform          || '';
        const ageVerified = !!(f?.ageVerified || u.ageVerified);
        const avHtml = image
            ? `<div class="iim-av" style="background-image:url('${cssUrl(image)}')"></div>`
            : `<div class="iim-av iim-av-letter">${esc(displayName[0].toUpperCase())}</div>`;
        const timerTd = hasTimers
            ? `<td style="text-align:right;font-size:11px;color:var(--tx3);padding-right:10px;">${esc(fmtTimer(u.joinedAt))}</td>`
            : '';
        const trust = getTrustRank(tags);
        const rankTd = `<td>${trust ? `<span class="vrcn-badge" style="color:${trust.color};border-color:${trust.color}20;background:${trust.color}18;font-size:10px;">${esc(trust.label)}</span>` : ''}</td>`;
        const dotCls = statusDotClass(status);
        const statusTd = `<td><div class="iim-status-cell">
            ${status ? `<span class="vrc-status-dot ${dotCls}" style="width:7px;height:7px;flex-shrink:0;"></span>` : ''}
            <span style="font-size:11px;">${esc(statusDesc || statusLabel(status))}</span>
        </div></td>`;
        let platIcon = '';
        if      (platform === 'standalonewindows') platIcon = `<span class="msi" title="${t('instance.platform.pc', 'PC')}" style="font-size:16px;color:var(--tx2);">computer</span>`;
        else if (platform === 'android')           platIcon = `<span class="msi" title="${t('instance.platform.quest', 'Quest')}" style="font-size:16px;color:var(--tx2);">view_in_ar</span>`;
        const platformTd = `<td style="text-align:center;">${platIcon}</td>`;
        const langTags  = tags.filter(t => t.startsWith('language_'));
        const langsHtml = langTags.map(t =>
            `<span class="vrcn-badge">${esc(LANG_MAP[t] || t.replace('language_', '').toUpperCase())}</span>`
        ).join('');
        const langTd  = `<td><div class="iim-lang-cell">${langsHtml}</div></td>`;
        const nameTd  = `<td><span class="iim-name">${esc(displayName)}</span></td>`;
        const ageTd   = `<td style="text-align:center;">${ageVerified ? `<span class="vrcn-badge" style="font-size:10px;color:#3ba55d;border-color:#3ba55d30;background:#3ba55d18;">18+</span>` : ''}</td>`;
        const rowClick = id ? ` style="cursor:pointer;" onclick="openFriendDetail('${jsq(id)}')"` : '';
        return `<tr class="iim-user-tr"${rowClick}><td style="width:40px;padding:5px 6px 5px 10px;">${avHtml}</td>${timerTd}${nameTd}${rankTd}${statusTd}${ageTd}${platformTd}${langTd}</tr>`;
    }

    const secLbl  = `font-size:10px;font-weight:700;color:var(--tx3);letter-spacing:.05em;`;
    const colSpan = hasTimers ? 8 : 7;
    let bodyRows = '';
    if (friendsEnriched.length > 0)
        bodyRows += `<tr><td colspan="${colSpan}" style="padding:10px 10px 4px;${secLbl}">${tf('instance.sections.friends_in_instance', { count: friendsEnriched.length }, 'FRIENDS IN INSTANCE ({count})')}</td></tr>` + friendsEnriched.map(makeRow).join('');
    if (othersEnriched.length > 0)
        bodyRows += `<tr><td colspan="${colSpan}" style="padding:10px 10px 4px;${secLbl}">${tf('instance.sections.players_in_instance', { count: othersEnriched.length }, 'PLAYERS IN INSTANCE ({count})')}</td></tr>` + othersEnriched.map(makeRow).join('');

    const tableHtml = enriched.length > 0
        ? `<div class="iim-scroll"><table class="iim-table">${thead}<tbody>${bodyRows}</tbody></table></div>`
        : `<div style="padding:14px 0;color:var(--tx3);font-size:12px;">${t('instance.no_player_data_available', 'No player data available.')}</div>`;

    const prevIimScroll = c.querySelector('.iim-scroll')?.scrollTop || 0;
    c.innerHTML = `${bannerHtml}
    <div class="fd-content${thumb ? ' fd-has-banner' : ''}" style="padding:16px;">
        <h2 style="margin:0 0 4px;color:var(--tx0);font-size:18px;">${esc(name)}</h2>
        <div class="fd-badges-row">
            <span class="vrcn-badge ${instCls}">${instLabel}</span>
            ${copyBadge}
            <span style="font-size:11px;color:var(--tx3);margin-left:4px;"><span class="msi" style="font-size:12px;vertical-align:-2px;">person</span> ${users.length || data.nUsers || 0}${data.capacity ? '/' + data.capacity : ''}</span>
        </div>
        ${tableHtml}
        <div class="fd-actions">
            <button class="vrcn-button-round" onclick="closeInstanceInfoModal();openInviteModal()"><span class="msi">person_add</span> ${t('instance.actions.invite', 'Invite')}</button>
            <button class="vrcn-button-round" onclick="closeInstanceInfoModal();openWorldSearchDetail('${wid}')">${t('dashboard.instances.open_world', 'Open World')}</button>
            <button class="vrcn-button-round" style="margin-left:auto;" onclick="closeInstanceInfoModal()">${t('common.close', 'Close')}</button>
        </div>
    </div>`;

    m.style.display = 'flex';
    if (prevIimScroll > 0) {
        const newIimScroll = c.querySelector('.iim-scroll');
        if (newIimScroll) newIimScroll.scrollTop = prevIimScroll;
    }
}

function closeInstanceInfoModal() {
    document.getElementById('modalInstanceInfo').style.display = 'none';
}

let _instanceInfoTimer = null;
function requestInstanceInfo() {
    if (!currentVrcUser) return;
    clearTimeout(_instanceInfoTimer);
    _instanceInfoTimer = setTimeout(() => sendToCS({ action: 'vrcGetCurrentInstance' }), 500);
}
