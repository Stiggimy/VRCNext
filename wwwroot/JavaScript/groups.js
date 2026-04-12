/* === Join State === */
function getJoinStateLabel(js) {
    const map = {
        open: ['groups.join_state.open', 'Open'],
        closed: ['groups.join_state.closed', 'Closed'],
        invite: ['groups.join_state.invite_only', 'Invite Only'],
        request: ['groups.join_state.request_invite', 'Request Invite'],
    };
    const entry = map[js];
    return entry ? t(entry[0], entry[1]) : (js || '?');
}

function getGroupMembersText(count) {
    return tf('worlds.groups.members', { count }, '{count} members');
}

function joinStateBadge(js) {
    const map = {
        open:    { label: getJoinStateLabel('open'), cls: 'public'  },
        closed:  { label: getJoinStateLabel('closed'), cls: 'private' },
        invite:  { label: getJoinStateLabel('invite'), cls: 'friends' },
        request: { label: getJoinStateLabel('request'), cls: 'group'   },
    };
    const m = map[js] || { label: getJoinStateLabel(js), cls: 'hidden' };
    return `<span class="vrcn-badge ${m.cls}">${esc(m.label)}</span>`;
}

/* === My Groups === */

function _renderGroupListCard(g) {
    const metaParts = [];
    if (g.shortCode) metaParts.push(esc(g.shortCode));
    metaParts.push(`<span class="msi" style="font-size:12px;">group</span> ${esc(getGroupMembersText(g.memberCount || 0))}`);
    const iconHtml = g.iconUrl ? `<div class="cc-group-icon" style="background-image:url('${cssUrl(g.iconUrl)}')"></div>` : '';
    return `<div class="vrcn-content-card" onclick="openGroupDetail('${esc(g.id)}')">
        <div class="cc-bg"><img src="${g.bannerUrl||'fallback_cover.png'}" onerror="this.src='fallback_cover.png'" style="position:absolute;inset:0;width:100%;height:100%;object-fit:cover;"></div>
        <div class="cc-scrim"></div>
        <div class="cc-content">
            <div class="cc-name">${esc(g.name)}</div>
            <div class="cc-bottom-row">
                <div class="cc-meta">${iconHtml}${metaParts.join(' · ')}</div>
                ${g.joinState ? joinStateBadge(g.joinState) : ''}
            </div>
        </div>
    </div>`;
}

function setGroupFilter(filter) {
    document.getElementById('groupFilterMine').classList.toggle('active', filter === 'mine');
    document.getElementById('groupFilterSearch').classList.toggle('active', filter === 'search');
    document.getElementById('groupMineArea').style.display   = filter === 'mine'   ? '' : 'none';
    document.getElementById('groupSearchArea').style.display = filter === 'search' ? '' : 'none';
    if (filter === 'mine' && !myGroupsLoaded) loadMyGroups();
    if (filter === 'search') document.getElementById('searchGroupsInput')?.focus();
}

document.documentElement.addEventListener('languagechange', () => {
    if (typeof myGroupsLoaded !== 'undefined' && myGroupsLoaded && typeof myGroups !== 'undefined' && Array.isArray(myGroups)) {
        filterMyGroups();
    }
    if (document.getElementById('gdTabInfo') && window._currentGroupDetailFull) {
        renderGroupDetail(window._currentGroupDetailFull);
    }
});

function filterMyGroups() {
    const q = (document.getElementById('filterGroupsInput')?.value || '').toLowerCase();
    const el = document.getElementById('myGroupsGrid');
    if (!el) return;
    const filtered = q
        ? myGroups.filter(g => (g.name||'').toLowerCase().includes(q) || (g.shortCode||'').toLowerCase().includes(q))
        : myGroups;
    el.innerHTML = filtered.length
        ? filtered.map(_renderGroupListCard).join('')
        : `<div class="empty-msg">${q ? t('groups.mine.empty_match', 'No groups match') : t('groups.mine.empty_joined', 'No groups joined')}</div>`;
}

function loadMyGroups() {
    sendToCS({ action: 'vrcGetMyGroups' });
}

function refreshGroups() {
    const btn = document.getElementById('groupsRefreshBtn');
    if (btn) { btn.disabled = true; btn.querySelector('.msi').textContent = 'hourglass_empty'; }
    sendToCS({ action: 'vrcGetMyGroups' });
}

function renderMyGroups(list) {
    const btn = document.getElementById('groupsRefreshBtn');
    if (btn) { btn.disabled = false; btn.querySelector('.msi').textContent = 'refresh'; }
    myGroups = list || [];
    myGroupsLoaded = true;
    filterMyGroups();
    if (typeof renderDashGroupActivity === 'function') renderDashGroupActivity();
}

function setGroupVisibility(groupId, visibility) {
    sendToCS({ action: 'vrcSetGroupVisibility', groupId, visibility });
    // Optimistic UI update
    ['visible','friends','hidden'].forEach(v => {
        const btn = document.getElementById('ggrpVis_' + v);
        if (!btn) return;
        const isActive = v === visibility;
        const icons = { visible: 'public', friends: 'people', hidden: 'visibility_off' };
        btn.classList.toggle('vrcn-btn-primary', isActive);
        btn.querySelector('.msi').textContent = isActive ? 'check_circle' : icons[v];
    });
    // Update myGroups cache so context menu reflects new value
    if (typeof myGroups !== 'undefined') {
        const entry = myGroups.find(g => g.id === groupId);
        if (entry) entry.visibility = visibility;
    }
}

function openGroupDetail(groupId) {
    const el = document.getElementById('detailModalContent');
    el.innerHTML = sk('detail');
    document.getElementById('modalDetail').style.display = 'flex';
    sendToCS({ action: 'vrcGetGroup', groupId });
}

function renderGroupDetail(g) {
    window._currentGroupDetailFull = g;
    window._currentGroupDetail = { id: g.id, canKick: g.canKick === true, canBan: g.canBan === true, canManageRoles: g.canManageRoles === true, canAssignRoles: g.canAssignRoles === true, languages: g.languages || [], links: g.links || [], joinState: g.joinState || '', roles: g.roles || [] };
    window._gdBannedLoaded = false;
    window._gdMemberRoleIds = {};
    const el = document.getElementById('detailModalContent');
    const canEdit = g.canEdit === true;
    const gidJs  = jsq(g.id);
    const banner = g.bannerUrl || g.iconUrl || 'fallback_cover.png';
    const bannerEditBtn = canEdit ? `<button class="myp-edit-btn" style="position:absolute;top:8px;right:8px;z-index:2;" onclick="openImagePicker('group-banner','${gidJs}')" title="${esc(t('groups.images.change_banner', 'Change banner'))}"><span class="msi" style="font-size:13px;">edit</span></button>` : '';
    const bannerHtml = banner
        ? `<div class="fd-banner">${bannerEditBtn}<img src="${banner}" onerror="this.src='fallback_cover.png'"><div class="fd-banner-fade"></div><button class="btn-notif" style="position:absolute;top:8px;right:8px;z-index:3;" title="${esc(t('common.share','Share'))}" onclick="navigator.clipboard.writeText('https://vrchat.com/home/group/${esc(g.id)}').then(()=>showToast(true,t('common.link_copied','Link copied!')))"><span class="msi" style="font-size:20px;">share</span></button></div>`
        : (canEdit ? `<div style="display:flex;justify-content:flex-end;padding:4px 0 2px 0;"><button class="myp-edit-btn" onclick="openImagePicker('group-banner','${gidJs}')" title="${esc(t('groups.images.add_banner', 'Add banner'))}"><span class="msi" style="font-size:13px;">edit</span><span style="font-size:11px;margin-left:3px;">${esc(t('groups.images.banner', 'Banner'))}</span></button></div>` : '');

    // Header
    const iconEditBtn = canEdit ? `<button class="myp-edit-btn" style="position:absolute;bottom:-4px;right:-4px;padding:2px;min-width:0;width:18px;height:18px;display:flex;align-items:center;justify-content:center;" onclick="openImagePicker('group-icon','${gidJs}')" title="${esc(t('groups.images.change_icon', 'Change icon'))}"><span class="msi" style="font-size:11px;">edit</span></button>` : '';
    const iconHtml = g.iconUrl
        ? `<div style="position:relative;display:inline-block;flex-shrink:0;"><img class="fd-avatar" src="${g.iconUrl}" onerror="this.style.display='none'">${iconEditBtn}</div>`
        : (canEdit ? `<div style="position:relative;display:inline-block;flex-shrink:0;"><div class="fd-avatar" style="display:flex;align-items:center;justify-content:center;font-size:20px;font-weight:700;color:var(--tx3);">${esc((g.name||'?')[0])}</div>${iconEditBtn}</div>` : '');
    const headerMeta = [g.shortCode ? esc(g.shortCode) : '', esc(getGroupMembersText(g.memberCount || 0))].filter(Boolean).join(' &middot; ');
    const ownerLabel = g.ownerDisplayName || '';
    const ownerHtml = (g.ownerId && ownerLabel)
        ? `<div style="font-size:12px;color:var(--tx3);margin-top:2px;margin-bottom:4px;">${t('worlds.meta.by', 'by')} <span onclick="document.getElementById('modalDetail').style.display='none';openFriendDetail('${jsq(g.ownerId)}')" style="display:inline-flex;align-items:center;padding:1px 8px;border-radius:20px;background:var(--bg-hover);font-size:11px;font-weight:600;color:var(--tx1);cursor:pointer;line-height:1.8;">${esc(ownerLabel)}</span></div>`
        : '';
    const headerHtml = `<div class="fd-content${banner ? ' fd-has-banner' : ''}"><div class="fd-header">${iconHtml}<div style="flex:1;min-width:0;"><div class="fd-name">${esc(g.name)}</div>${ownerHtml}<div class="fd-status">${headerMeta}</div></div><span id="ggrpHeaderBadge" style="margin-left:auto;flex-shrink:0;">${g.joinState ? joinStateBadge(g.joinState) : ''}</span></div><div class="fd-badges-row">${idBadge(g.id)}</div>`;

    // Actions - moved to bottom bar
    const canPost   = g.canPost === true;
    const canEvent  = g.canEvent === true;
    const canInvite = g.canInvite === true;
    const inviteBtn = (g.isJoined && canInvite)
        ? `<button class="vrcn-button-round vrcn-btn-join" onclick="openGroupInviteModal('${esc(g.id)}')"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">person_add</span>${t('groups.actions.invite', 'Invite')}</button>`
        : '';
    const createPostBtn = (g.isJoined && canPost)
        ? `<button class="vrcn-button-round vrcn-btn-join" onclick="openGroupPostModal('${esc(g.id)}')"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">edit</span>${t('groups.actions.post', 'Post')}</button>`
        : '';
    const createEventBtn = (g.isJoined && canEvent)
        ? `<button class="vrcn-button-round vrcn-btn-join" onclick="openGroupEventModal('${esc(g.id)}')"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">event</span>${t('groups.actions.events', 'Events')}</button>`
        : '';
    const leaveJoinBtn = g.isJoined
        ? `<button class="vrcn-button-round vrcn-btn-danger" onclick="sendToCS({action:'vrcLeaveGroup',groupId:'${esc(g.id)}'});document.getElementById('modalDetail').style.display='none';"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">logout</span>${t('groups.actions.leave_group', 'Leave Group')}</button>`
        : `<button class="vrcn-button-round vrcn-btn-join" onclick="sendToCS({action:'vrcJoinGroup',groupId:'${esc(g.id)}'});document.getElementById('modalDetail').style.display='none';"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">group_add</span>${t('groups.actions.join_group', 'Join Group')}</button>`;

    // Tab: Info
    const gid_e = esc(g.id);
    const grpLangs = (g.languages || []);
    const grpLinks = (g.links || []).filter(Boolean);
    const grpLangsViewHtml = grpLangs.length
        ? `<div class="fd-lang-tags">${grpLangs.map(l => `<span class="vrcn-badge">${esc(LANG_MAP['language_'+l] || l.toUpperCase())}</span>`).join('')}</div>`
        : `<div class="myp-empty">${t('profiles.my_profile.empty.no_languages', 'No languages set')}</div>`;
    const grpLinksViewHtml = grpLinks.length
        ? `<div class="fd-bio-links">${grpLinks.map(url => renderBioLink(url)).join('')}</div>`
        : `<div class="myp-empty">${t('profiles.my_profile.empty.no_links', 'No links added')}</div>`;
    const infoTab = `
        <div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">${t('groups.sections.description', 'Description')}</span>
                ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('desc')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            <div id="gdescDescView">
                ${g.description ? `<div class="fd-bio">${esc(g.description)}</div>` : `<div class="myp-empty">${t('groups.empty.no_description', 'No description')}</div>`}
            </div>
            ${canEdit ? `<div id="gdescDescEdit" style="display:none;">
                <textarea id="gdescDescInput" class="myp-textarea" rows="4" maxlength="2000" placeholder="${esc(t('groups.placeholders.description', 'Group description...'))}">${esc(g.description||'')}</textarea>
                <div class="myp-edit-actions">
                    <button class="vrcn-button" onclick="cancelGroupField('desc')">${t('common.cancel', 'Cancel')}</button>
                    <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('desc','${gid_e}')">${t('common.save', 'Save')}</button>
                </div>
            </div>` : ''}
        </div>
        <div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">${t('groups.sections.links', 'Links')}</span>
                ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('links')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            <div id="ggrpLinksView">${grpLinksViewHtml}</div>
            ${canEdit ? `<div id="ggrpLinksEdit" style="display:none;">
                <div id="ggrpLinksInputs"></div>
                <div class="myp-edit-actions">
                    <button class="vrcn-button" onclick="cancelGroupField('links')">${t('common.cancel', 'Cancel')}</button>
                    <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('links','${gid_e}')">${t('common.save', 'Save')}</button>
                </div>
            </div>` : ''}
        </div>
        <div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">${t('groups.sections.languages', 'Languages')}</span>
                ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('langs')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            <div id="ggrpLangsView">${grpLangsViewHtml}</div>
            ${canEdit ? `<div id="ggrpLangsEdit" style="display:none;">
                <div id="ggrpLangsChips" class="myp-lang-chips"></div>
                <div class="myp-lang-add-row">
                    <select id="ggrpLangSelect" class="myp-lang-select"><option value="">${t('profiles.my_profile.add_language', 'Add language...')}</option></select>
                    <button class="myp-add-lang-btn" onclick="addGrpLanguage()"><span class="msi" style="font-size:15px;">add</span></button>
                </div>
                <div class="myp-edit-actions">
                    <button class="vrcn-button" onclick="cancelGroupField('langs')">${t('common.cancel', 'Cancel')}</button>
                    <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('langs','${gid_e}')">${t('common.save', 'Save')}</button>
                </div>
            </div>` : ''}
        </div>
        <div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">${t('groups.sections.rules', 'Rules')}</span>
                ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('rules')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            <div id="gdescRulesView">
                ${g.rules ? `<div style="font-size:11px;color:var(--tx3);padding:8px;background:var(--bg-input);border-radius:8px;max-height:120px;overflow-y:auto;white-space:pre-wrap;">${esc(g.rules)}</div>` : `<div class="myp-empty">${t('groups.empty.no_rules', 'No rules set')}</div>`}
            </div>
            ${canEdit ? `<div id="gdescRulesEdit" style="display:none;">
                <textarea id="gdescRulesInput" class="myp-textarea" rows="5" maxlength="2000" placeholder="${esc(t('groups.placeholders.rules', 'Group rules...'))}">${esc(g.rules||'')}</textarea>
                <div class="myp-edit-actions">
                    <button class="vrcn-button" onclick="cancelGroupField('rules')">${t('common.cancel', 'Cancel')}</button>
                    <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('rules','${gid_e}')">${t('common.save', 'Save')}</button>
                </div>
            </div>` : ''}
        </div>
        <div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">${t('groups.sections.open_members', 'Open to new Members')}</span>
                ${canEdit ? `<button class="myp-edit-btn" onclick="editGroupField('joinState')"><span class="msi" style="font-size:14px;">edit</span></button>` : ''}
            </div>
            <div id="ggrpJoinStateView">
                ${g.joinState ? joinStateBadge(g.joinState) : `<div class="myp-empty">${t('groups.empty.not_set', 'Not set')}</div>`}
            </div>
            ${canEdit ? `<div id="ggrpJoinStateEdit" style="display:none;">
                <select id="ggrpJoinStateSelect" class="myp-lang-select" style="width:100%;margin-bottom:6px;">
                    <option value="open"    ${g.joinState==='open'    ? 'selected' : ''}>${t('groups.join_state.open', 'Open')}</option>
                    <option value="closed"  ${g.joinState==='closed'  ? 'selected' : ''}>${t('groups.join_state.closed', 'Closed')}</option>
                    <option value="invite"  ${g.joinState==='invite'  ? 'selected' : ''}>${t('groups.join_state.invite_only', 'Invite Only')}</option>
                    <option value="request" ${g.joinState==='request' ? 'selected' : ''}>${t('groups.join_state.request_invite', 'Request Invite')}</option>
                </select>
                <div class="myp-edit-actions">
                    <button class="vrcn-button" onclick="cancelGroupField('joinState')">${t('common.cancel', 'Cancel')}</button>
                    <button class="vrcn-button vrcn-btn-primary" onclick="saveGroupField('joinState','${gid_e}')">${t('common.save', 'Save')}</button>
                </div>
            </div>` : ''}
        </div>
        ${g.isJoined ? `<div class="myp-section">
            <div class="myp-section-header">
                <span class="myp-section-title">${t('groups.visibility.title', 'Visibility')}</span>
            </div>
            <div id="ggrpVisibilityBtns" style="display:flex;gap:6px;flex-wrap:wrap;">
                ${[
                    { val: 'visible', icon: 'public',         key: 'groups.visibility.visible', fb: 'Visible for Everyone' },
                    { val: 'friends', icon: 'people',         key: 'groups.visibility.friends', fb: 'Visible for Friends'  },
                    { val: 'hidden',  icon: 'visibility_off', key: 'groups.visibility.hidden',  fb: 'Visible for None'     },
                ].map(opt => {
                    const active = (g.visibility || 'visible') === opt.val;
                    return `<button class="vrcn-button${active ? ' vrcn-btn-primary' : ''}" id="ggrpVis_${opt.val}" onclick="setGroupVisibility('${gid_e}','${opt.val}')" style="flex:1;justify-content:center;min-width:0;font-size:11px;">
                        <span class="msi" style="font-size:13px;">${active ? 'check_circle' : opt.icon}</span>
                        ${esc(t(opt.key, opt.fb))}
                    </button>`;
                }).join('')}
            </div>
        </div>` : ''}`;

    // Tab: Posts
    const posts = g.posts || [];
    let postsTab = '';
    if (posts.length === 0) {
        postsTab = renderGroupEmptyMessage('groups.empty.no_posts', 'No posts');
    } else {
        posts.forEach((p, i) => {
            const date = p.createdAt ? fmtShortDate(new Date(p.createdAt)) : '';
            const imgHtml = p.imageUrl ? `<img src="${p.imageUrl}" style="width:100%;border-radius:6px;margin-top:8px;" onerror="this.style.display='none'">` : '';
            const fullText = p.text || '';
            const isLong = fullText.length > 120;
            const preview = isLong ? fullText.slice(0, 120) + '...' : fullText;
            const pid = esc(p.id || ''), gid = esc(g.id || '');
            const delBtn = (canPost && p.id)
                ? `<button class="gd-post-del" onclick="deleteGroupPost('${gid}','${pid}',this)" title="${esc(t('groups.posts.delete_title', 'Delete post'))}"><span class="msi">delete</span></button>`
                : '';
            postsTab += `<div class="fd-group-card" data-post-id="${pid}" style="display:block;cursor:default;padding:12px;">
                <div style="display:flex;align-items:center;gap:6px;margin-bottom:2px;">
                    <div style="font-size:13px;font-weight:600;color:var(--tx0);flex:1;">${esc(p.title || 'Untitled')}</div>
                    ${delBtn}
                </div>
                <div style="font-size:10px;color:var(--tx3);margin-bottom:6px;">${date}${p.visibility ? ' · ' + esc(p.visibility) : ''}</div>
                <div class="gd-post-text" id="gpost${i}" data-full="${esc(fullText).replace(/"/g,'&quot;')}" data-preview="${esc(preview).replace(/"/g,'&quot;')}" style="font-size:12px;color:var(--tx2);line-height:1.4;">${esc(preview)}</div>
                ${isLong ? `<div style="margin-top:4px;"><span class="gd-expand" data-expanded="0" onclick="toggleGPost(${i})">${t('groups.posts.show_more', 'Show more')}</span></div>` : ''}
                ${imgHtml}
            </div>`;
        });
    }

    // Tab: Events
    const events = g.groupEvents || [];
    let eventsTab = '';
    if (events.length === 0) {
        eventsTab = renderGroupEmptyMessage('groups.empty.no_events', 'No events');
    } else {
        events.forEach(e => {
            const startD = e.startsAt ? new Date(e.startsAt) : null;
            const endD   = e.endsAt   ? new Date(e.endsAt)   : null;
            const endDiffDay = endD && !isNaN(endD) && startD &&
                (endD.getFullYear() !== startD.getFullYear() || endD.getMonth() !== startD.getMonth() || endD.getDate() !== startD.getDate());
            const timeStr = startD && !isNaN(startD)
                ? fmtTime(startD) + (endD && !isNaN(endD) ? ' – ' + (endDiffDay ? fmtLongDate(endD) + ', ' : '') + fmtTime(endD) : '')
                : '';
            const dateStr = startD && !isNaN(startD) ? fmtLongDate(startD) : '';
            const imgHtml = e.imageUrl ? `<img src="${e.imageUrl}" style="width:100%;max-height:120px;object-fit:cover;border-radius:6px;margin-bottom:8px;" onerror="this.style.display='none'">` : '';
            const badge = e.accessType ? `<span style="font-size:9px;padding:1px 6px;border-radius:4px;background:color-mix(in srgb,var(--accent) 12%,transparent);color:var(--accent-lt);border:1px solid color-mix(in srgb,var(--accent) 35%,transparent);margin-left:6px;">${esc(e.accessType)}</span>` : '';
            const gid = esc(e.ownerId || g.id || '');
            const cid = esc(e.id || '');
            const delEvtBtn = (canEvent && e.id)
                ? `<button class="gd-post-del" onclick="event.stopPropagation();deleteGroupEvent('${esc(g.id)}','${cid}',this)" title="${esc(t('groups.events.delete_title', 'Delete event'))}"><span class="msi">delete</span></button>`
                : '';
            eventsTab += `<div class="fd-group-card" data-event-id="${cid}" style="display:block;cursor:pointer;padding:12px;" onclick="openEventDetail('${gid}','${cid}')">
                ${imgHtml}
                <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;">
                    <div style="font-size:13px;font-weight:600;color:var(--tx0);">${esc(e.title || 'Untitled Event')}${badge}</div>
                    ${delEvtBtn}
                </div>
                <div style="font-size:10px;color:var(--tx3);margin:2px 0 4px;">${dateStr}${timeStr ? ' · ' + timeStr : ''}</div>
                ${e.description ? `<div style="font-size:12px;color:var(--tx2);line-height:1.4;">${esc(e.description)}</div>` : ''}
            </div>`;
        });
    }

    // Tab: Instances
    const instances = g.groupInstances || [];
    let instancesTab = '';
    if (instances.length === 0) {
        instancesTab = `<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">${t('groups.empty.no_active_instances', 'No active instances')}</div>`;
    } else {
        instances.forEach(inst => {
            const thumbHtml = inst.worldThumb ? `<img style="width:48px;height:48px;border-radius:8px;object-fit:cover;flex-shrink:0;" src="${inst.worldThumb}" onerror="this.style.display='none'">` : '';
            const users = inst.userCount > 0
                ? (inst.capacity > 0
                    ? `${inst.userCount}/${inst.capacity}`
                    : (inst.userCount === 1
                        ? tf('groups.instances.user_one', { count: inst.userCount }, '{count} user')
                        : tf('groups.instances.user_other', { count: inst.userCount }, '{count} users')))
                : '';
            const loc = (inst.location || '').replace(/'/g, "\\'");
            instancesTab += `<div class="fd-group-card" onclick="sendToCS({action:'vrcJoinFriend',location:'${loc}'})">
                ${thumbHtml}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(inst.worldName || t('dashboard.instances.unknown_world', 'Unknown World'))}</div><div class="fd-group-card-meta">${users}</div></div>
                <button class="vrcn-button-round vrcn-btn-join" onclick="event.stopPropagation();sendToCS({action:'vrcJoinFriend',location:'${loc}'})"><span class="msi" style="font-size:14px;">login</span>${t('common.join', 'Join')}</button>
            </div>`;
        });
    }

    // Tab: Gallery
    const gallery = g.galleryImages || [];
    let galleryTab = '';
    if (gallery.length === 0) {
        galleryTab = renderGroupEmptyMessage('groups.empty.no_gallery_images', 'No gallery images');
    } else {
        galleryTab = '<div class="gd-gallery-grid">';
        gallery.forEach(img => {
            if (img.imageUrl) galleryTab += `<img class="gd-gallery-img" src="${img.imageUrl}" onclick="openLightbox('${jsq(img.imageUrl)}')" onerror="this.style.display='none'">`;
        });
        galleryTab += '</div>';
    }

    // Tab: Members (paginated)
    const members = g.groupMembers || [];
    let membersTab = `<div class="search-bar-row" style="margin-bottom:6px;">
        <span class="msi search-ico">search</span>
        <input id="gdMembersSearch" type="text" class="vrcn-input" placeholder="${esc(t('groups.members.search_placeholder', 'Search users by name... hit enter'))}" style="background:var(--bg-input);" onkeydown="if(event.key==='Enter')searchGroupMembers()">
    </div>`;
    membersTab += '<div id="gdMembersList" style="display:grid;grid-template-columns:1fr 1fr;column-gap:6px;">';
    if (members.length === 0) {
        membersTab += renderGroupEmptyMessage('groups.empty.no_members', 'No members');
    } else {
        members.forEach(m => { membersTab += renderGroupMemberCard(m); });
    }
    membersTab += '</div>';
    membersTab += `<div id="gdMembersLoadMore" style="text-align:center;padding:12px;">` +
        (members.length >= 50
            ? `<button class="vrcn-button" onclick="loadMoreGroupMembers()">${t('groups.members.load_more', 'Load More Members')}</button>`
            : (members.length > 0 ? `<div style="font-size:11px;color:var(--tx3);">${t('groups.members.all_loaded', 'All members loaded')}</div>` : '')) +
        `</div>`;
    // Store group id + offset for pagination
    window._gdMembersGroupId = g.id;
    window._gdMembersOffset = members.length;
    window._gdMembersSearchActive = false;

    // Tabs
    const tabs = [
        { key: 'info', label: t('groups.tabs.info', 'Info') },
        { key: 'posts', label: t('groups.tabs.posts', 'Posts') },
        { key: 'events', label: t('groups.tabs.events', 'Events') },
        { key: 'instances', label: t('groups.tabs.live', 'Live') },
        { key: 'gallery', label: t('groups.tabs.gallery', 'Gallery') },
        { key: 'members', label: t('groups.tabs.members', 'Members') },
    ];
    if (g.canManageRoles) tabs.push({ key: 'roles', label: t('groups.tabs.roles', 'Roles') });
    if (g.canBan)         tabs.push({ key: 'banned', label: t('groups.tabs.banned', 'Banned') });
    const tabsHtml = `<div class="fd-tabs gd-tabs">${tabs.map((t,i) => `<button class="fd-tab${i===0?' active':''}" onclick="switchGdTab('${t.key}',this)">${t.label}</button>`).join('')}</div>`;

    const rolesTab   = g.canManageRoles ? _buildRolesTab(g) : '';
    const bannedTab  = g.canBan ? _buildBannedTab() : '';

    el.innerHTML = `${bannerHtml}${headerHtml}${tabsHtml}
        <div id="gdTabInfo">${infoTab}</div>
        <div id="gdTabPosts" style="display:none;">${postsTab}</div>
        <div id="gdTabEvents" style="display:none;">${eventsTab}</div>
        <div id="gdTabInstances" style="display:none;">${instancesTab}</div>
        <div id="gdTabGallery" style="display:none;">${galleryTab}</div>
        <div id="gdTabMembers" style="display:none;">${membersTab}</div>
        ${g.canManageRoles ? `<div id="gdTabRoles" style="display:none;">${rolesTab}</div>` : ''}
        ${g.canBan ? `<div id="gdTabBanned" style="display:none;">${bannedTab}</div>` : ''}
        <div style="margin-top:10px;display:flex;justify-content:space-between;align-items:center;"><div style="display:flex;gap:8px;">${inviteBtn}${createPostBtn}${createEventBtn}${leaveJoinBtn}</div><button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'">${t('common.close', 'Close')}</button></div>
    </div>`;
    applyGroupDetailTranslations(g);
}

function renderGroupEmptyMessage(key, fallback) {
    return `<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">${t(key, fallback)}</div>`;
}

function setGroupEditActionLabels(panelId) {
    const panel = document.getElementById(panelId);
    if (!panel) return;
    const buttons = panel.querySelectorAll('.myp-edit-actions button');
    if (buttons[0]) buttons[0].textContent = t('common.cancel', 'Cancel');
    if (buttons[1]) buttons[1].textContent = t('common.save', 'Save');
}

function applyGroupDetailTranslations(g) {
    const detail = document.getElementById('detailModalContent');
    if (!detail) return;

    const statusEl = detail.querySelector('.fd-status');
    if (statusEl) {
        const parts = [];
        if (g.shortCode) parts.push(g.shortCode);
        parts.push(getGroupMembersText(g.memberCount || 0));
        statusEl.textContent = parts.join(' - ');
    }

    const tabKeys = [
        ['info', 'groups.tabs.info', 'Info'],
        ['posts', 'groups.tabs.posts', 'Posts'],
        ['events', 'groups.tabs.events', 'Events'],
        ['instances', 'groups.tabs.live', 'Live'],
        ['gallery', 'groups.tabs.gallery', 'Gallery'],
        ['members', 'groups.tabs.members', 'Members'],
        ['roles', 'groups.tabs.roles', 'Roles'],
        ['banned', 'groups.tabs.banned', 'Banned'],
    ];
    tabKeys.forEach(([key, i18nKey, fallback]) => {
        const btn = detail.querySelector(`.gd-tabs .fd-tab[onclick*="switchGdTab('${key}'"]`);
        if (btn) btn.textContent = t(i18nKey, fallback);
    });

    const titles = detail.querySelectorAll('#gdTabInfo .myp-section-title');
    if (titles[0]) titles[0].textContent = t('groups.sections.description', 'Description');
    if (titles[1]) titles[1].textContent = t('groups.sections.links', 'Links');
    if (titles[2]) titles[2].textContent = t('groups.sections.languages', 'Languages');
    if (titles[3]) titles[3].textContent = t('groups.sections.rules', 'Rules');
    if (titles[4]) titles[4].textContent = t('groups.sections.open_members', 'Open to new Members');

    if (!g.description) {
        const descView = document.getElementById('gdescDescView');
        if (descView) descView.innerHTML = `<div class="myp-empty">${t('groups.empty.no_description', 'No description')}</div>`;
    }
    if (!(g.links || []).filter(Boolean).length) {
        const linksView = document.getElementById('ggrpLinksView');
        if (linksView) linksView.innerHTML = `<div class="myp-empty">${t('profiles.my_profile.empty.no_links', 'No links added')}</div>`;
    }
    if (!(g.languages || []).length) {
        const langsView = document.getElementById('ggrpLangsView');
        if (langsView) langsView.innerHTML = `<div class="myp-empty">${t('profiles.my_profile.empty.no_languages', 'No languages set')}</div>`;
    }
    if (!g.rules) {
        const rulesView = document.getElementById('gdescRulesView');
        if (rulesView) rulesView.innerHTML = `<div class="myp-empty">${t('groups.empty.no_rules', 'No rules set')}</div>`;
    }
    if (!g.joinState) {
        const joinStateView = document.getElementById('ggrpJoinStateView');
        if (joinStateView) joinStateView.innerHTML = `<div class="myp-empty">${t('groups.empty.not_set', 'Not set')}</div>`;
    }

    const descInput = document.getElementById('gdescDescInput');
    if (descInput) descInput.placeholder = t('groups.placeholders.description', 'Group description...');
    const rulesInput = document.getElementById('gdescRulesInput');
    if (rulesInput) rulesInput.placeholder = t('groups.placeholders.rules', 'Group rules...');
    setGroupEditActionLabels('gdescDescEdit');
    setGroupEditActionLabels('ggrpLinksEdit');
    setGroupEditActionLabels('ggrpLangsEdit');
    setGroupEditActionLabels('gdescRulesEdit');
    setGroupEditActionLabels('ggrpJoinStateEdit');

    const joinStateSelect = document.getElementById('ggrpJoinStateSelect');
    if (joinStateSelect) {
        Array.from(joinStateSelect.options).forEach(opt => {
            if (opt.value === 'open') opt.textContent = t('groups.join_state.open', 'Open');
            if (opt.value === 'closed') opt.textContent = t('groups.join_state.closed', 'Closed');
            if (opt.value === 'invite') opt.textContent = t('groups.join_state.invite_only', 'Invite Only');
            if (opt.value === 'request') opt.textContent = t('groups.join_state.request_invite', 'Request Invite');
        });
    }

    const invBtn = detail.querySelector(`button[onclick*="openGroupInviteModal("]`);
    if (invBtn) invBtn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">person_add</span>${t('groups.actions.invite', 'Invite')}`;
    const postBtn = detail.querySelector(`button[onclick*="openGroupPostModal("]`);
    if (postBtn) postBtn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">edit</span>${t('groups.actions.post', 'Post')}`;
    const eventBtn = detail.querySelector(`button[onclick*="openGroupEventModal("]`);
    if (eventBtn) eventBtn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">event</span>${t('groups.actions.events', 'Events')}`;
    const leaveBtn = detail.querySelector(`button[onclick*="vrcLeaveGroup"]`);
    if (leaveBtn) leaveBtn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">logout</span>${t('groups.actions.leave_group', 'Leave Group')}`;
    const joinBtn = detail.querySelector(`button[onclick*="vrcJoinGroup"]`);
    if (joinBtn) joinBtn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">group_add</span>${t('groups.actions.join_group', 'Join Group')}`;
    const footerRow = Array.from(detail.querySelectorAll('div')).find(el => el.getAttribute('style')?.includes('justify-content:space-between'));
    if (footerRow) {
        const footerButtons = footerRow.querySelectorAll('button.vrcn-button-round');
        const closeBtn = footerButtons[footerButtons.length - 1];
        if (closeBtn) closeBtn.textContent = t('common.close', 'Close');
    }

    const postsTab = document.getElementById('gdTabPosts');
    if (postsTab) {
        if (!(g.posts || []).length) postsTab.innerHTML = renderGroupEmptyMessage('groups.empty.no_posts', 'No posts');
        postsTab.querySelectorAll('.gd-post-del').forEach(btn => btn.title = t('groups.posts.delete_title', 'Delete post'));
        postsTab.querySelectorAll('.gd-expand').forEach(link => {
            if (!link.dataset.expanded) link.dataset.expanded = '0';
            link.textContent = link.dataset.expanded === '1'
                ? t('groups.posts.show_less', 'Show less')
                : t('groups.posts.show_more', 'Show more');
        });
    }

    const eventsTab = document.getElementById('gdTabEvents');
    if (eventsTab) {
        if (!(g.groupEvents || []).length) eventsTab.innerHTML = renderGroupEmptyMessage('groups.empty.no_events', 'No events');
        eventsTab.querySelectorAll('.gd-post-del').forEach(btn => btn.title = t('groups.events.delete_title', 'Delete event'));
    }

    const instancesTab = document.getElementById('gdTabInstances');
    if (instancesTab) {
        if (!(g.groupInstances || []).length) {
            instancesTab.innerHTML = renderGroupEmptyMessage('groups.empty.no_active_instances', 'No active instances');
        } else {
            instancesTab.querySelectorAll('.fd-group-card-name').forEach(el => {
                if (el.textContent.trim() === 'Unknown World') el.textContent = t('dashboard.instances.unknown_world', 'Unknown World');
            });
            instancesTab.querySelectorAll('.vrcn-button-round').forEach(btn => {
                btn.innerHTML = `<span class="msi" style="font-size:14px;">login</span>${t('common.join', 'Join')}`;
            });
        }
    }

    const galleryTab = document.getElementById('gdTabGallery');
    if (galleryTab && !(g.galleryImages || []).length) galleryTab.innerHTML = renderGroupEmptyMessage('groups.empty.no_gallery_images', 'No gallery images');

    const membersSearch = document.getElementById('gdMembersSearch');
    if (membersSearch) membersSearch.placeholder = t('groups.members.search_placeholder', 'Search users by name... hit enter');
    const membersList = document.getElementById('gdMembersList');
    if (membersList && !(g.groupMembers || []).length) membersList.innerHTML = renderGroupEmptyMessage('groups.empty.no_members', 'No members');
    const loadMore = document.getElementById('gdMembersLoadMore');
    if (loadMore) {
        if ((g.groupMembers || []).length >= 50) {
            const btn = loadMore.querySelector('button');
            if (btn) btn.textContent = t('groups.members.load_more', 'Load More Members');
        } else if ((g.groupMembers || []).length > 0) {
            loadMore.innerHTML = `<div style="font-size:11px;color:var(--tx3);">${t('groups.members.all_loaded', 'All members loaded')}</div>`;
        }
    }
}

function renderGroupMemberCard(m) {
    if (m.id && m.roleIds) {
        if (!window._gdMemberRoleIds) window._gdMemberRoleIds = {};
        window._gdMemberRoleIds[m.id] = m.roleIds;
    }
    return renderProfileItem(m, `closeDetailModal();openFriendDetail('${jsq(m.id || '')}')`);
}

const _grpFieldIds = {
    desc:      { view: 'gdescDescView',       edit: 'gdescDescEdit'       },
    rules:     { view: 'gdescRulesView',      edit: 'gdescRulesEdit'      },
    links:     { view: 'ggrpLinksView',       edit: 'ggrpLinksEdit'       },
    langs:     { view: 'ggrpLangsView',       edit: 'ggrpLangsEdit'       },
    joinState: { view: 'ggrpJoinStateView',   edit: 'ggrpJoinStateEdit'   },
};

function editGroupField(field) {
    Object.keys(_grpFieldIds).forEach(f => {
        if (f === field) return;
        const ids = _grpFieldIds[f];
        const v = document.getElementById(ids.view); if (v) v.style.display = '';
        const e = document.getElementById(ids.edit); if (e) e.style.display = 'none';
    });
    const ids = _grpFieldIds[field];
    if (!ids) return;
    document.getElementById(ids.view).style.display = 'none';
    document.getElementById(ids.edit).style.display = '';
    if (field === 'desc')  document.getElementById('gdescDescInput')?.focus();
    if (field === 'rules') document.getElementById('gdescRulesInput')?.focus();
    if (field === 'links') _renderGrpLinksInputs();
    if (field === 'langs') _renderGrpLangsEdit();
}

function cancelGroupField(field) {
    const ids = _grpFieldIds[field];
    if (!ids) return;
    document.getElementById(ids.view).style.display = '';
    document.getElementById(ids.edit).style.display = 'none';
}

function saveGroupField(field, groupId) {
    const ids = _grpFieldIds[field];
    const saveBtn = document.querySelector(`#${ids.edit} .vrcn-btn-primary`);
    if (saveBtn) saveBtn.disabled = true;

    if (field === 'desc') {
        sendToCS({ action: 'vrcUpdateGroup', groupId, description: document.getElementById('gdescDescInput')?.value ?? '' });
    } else if (field === 'rules') {
        sendToCS({ action: 'vrcUpdateGroup', groupId, rules: document.getElementById('gdescRulesInput')?.value ?? '' });
    } else if (field === 'links') {
        const inputs = document.querySelectorAll('#ggrpLinksInputs .vrcn-edit-field');
        const links = Array.from(inputs).map(i => i.value.trim()).filter(Boolean);
        sendToCS({ action: 'vrcUpdateGroup', groupId, links });
    } else if (field === 'langs') {
        const chips = document.querySelectorAll('#ggrpLangsChips [data-lang]');
        const languages = Array.from(chips).map(c => c.dataset.lang);
        sendToCS({ action: 'vrcUpdateGroup', groupId, languages });
    } else if (field === 'joinState') {
        const val = document.getElementById('ggrpJoinStateSelect')?.value;
        if (val) sendToCS({ action: 'vrcUpdateGroup', groupId, joinState: val });
    }
}

function _renderGrpLinksInputs() {
    const container = document.getElementById('ggrpLinksInputs');
    if (!container) return;
    const links = (window._currentGroupDetail?.links || []).filter(Boolean);
    container.innerHTML = [0, 1, 2].map(i =>
        `<div class="myp-link-row">
            <span class="myp-link-num">${i + 1}</span>
            <input type="url" class="vrcn-edit-field" placeholder="https://..." value="${esc(links[i]||'')}" maxlength="512" style="flex:1;">
        </div>`
    ).join('');
}

function _renderGrpLangsEdit() {
    const selected = (window._currentGroupDetail?.languages || []);
    _renderGrpLangChips(selected, document.getElementById('ggrpLangsChips'));
    const sel = document.getElementById('ggrpLangSelect');
    if (!sel) return;
    sel.innerHTML = `<option value="">${t('profiles.my_profile.add_language', 'Add language...')}</option>`;
    Object.entries(LANG_MAP).forEach(([key, name]) => {
        const code = key.replace('language_', '');
        if (!selected.includes(code))
            sel.insertAdjacentHTML('beforeend', `<option value="${code}">${esc(name)}</option>`);
    });
}

function _renderGrpLangChips(langs, el) {
    if (!el) return;
    el.innerHTML = langs.map(code =>
        `<span class="myp-lang-chip" data-lang="${code}">${esc(LANG_MAP['language_'+code] || code.toUpperCase())}<button class="myp-lang-remove" onclick="removeGrpLanguage('${code}')"><span class="msi" style="font-size:11px;">close</span></button></span>`
    ).join('');
}

function addGrpLanguage() {
    const sel = document.getElementById('ggrpLangSelect');
    const code = sel?.value;
    if (!code) return;
    const chips = Array.from(document.querySelectorAll('#ggrpLangsChips [data-lang]')).map(c => c.dataset.lang);
    if (chips.includes(code)) return;
    chips.push(code);
    _renderGrpLangChips(chips, document.getElementById('ggrpLangsChips'));
    const opt = sel.querySelector(`option[value="${code}"]`);
    if (opt) opt.remove();
    sel.value = '';
}

function removeGrpLanguage(code) {
    const chips = Array.from(document.querySelectorAll('#ggrpLangsChips [data-lang]')).map(c => c.dataset.lang).filter(c => c !== code);
    _renderGrpLangChips(chips, document.getElementById('ggrpLangsChips'));
    const sel = document.getElementById('ggrpLangSelect');
    if (sel) sel.insertAdjacentHTML('beforeend', `<option value="${code}">${esc(LANG_MAP['language_'+code] || code.toUpperCase())}</option>`);
}

function loadMoreGroupMembers() {
    if (!window._gdMembersGroupId || window._gdMembersSearchActive) return;
    const btn = document.querySelector('#gdMembersLoadMore button');
    if (btn) { btn.textContent = t('common.loading', 'Loading...'); btn.disabled = true; }
    sendToCS({ action: 'vrcGetGroupMembers', groupId: window._gdMembersGroupId, offset: window._gdMembersOffset || 0 });
}

function searchGroupMembers() {
    if (!window._gdMembersGroupId) return;
    const q = document.getElementById('gdMembersSearch')?.value.trim() || '';
    if (!q) {
        // Empty search → reset to normal paginated view
        window._gdMembersSearchActive = false;
        window._gdMembersOffset = 0;
        const list = document.getElementById('gdMembersList');
        if (list) list.innerHTML = renderGroupEmptyMessage('common.loading', 'Loading...');
        const lm = document.getElementById('gdMembersLoadMore');
        if (lm) lm.innerHTML = '';
        sendToCS({ action: 'vrcGetGroupMembers', groupId: window._gdMembersGroupId, offset: 0 });
        return;
    }
    window._gdMembersSearchActive = true;
    const list = document.getElementById('gdMembersList');
    if (list) list.innerHTML = renderGroupEmptyMessage('groups.members.searching', 'Searching...');
    const lm = document.getElementById('gdMembersLoadMore');
    if (lm) lm.innerHTML = '';
    sendToCS({ action: 'vrcSearchGroupMembers', groupId: window._gdMembersGroupId, query: q });
}

function switchGdTab(tab, btn) {
    ['Info','Posts','Events','Instances','Gallery','Members','Roles','Banned'].forEach(t => {
        const el = document.getElementById('gdTab' + t);
        if (el) el.style.display = t.toLowerCase() === tab ? '' : 'none';
    });
    btn.closest('.fd-tabs').querySelectorAll('.fd-tab').forEach(t => t.classList.remove('active'));
    btn.classList.add('active');
    if (tab === 'banned' && !window._gdBannedLoaded) loadGroupBans();
}

function toggleGPost(i) {
    const el = document.getElementById('gpost' + i);
    const link = el?.parentElement?.querySelector('.gd-expand');
    if (!el || !link) return;
    const expanded = link.dataset.expanded === '1';
    if (!expanded) {
        el.textContent = el.dataset.full;
        link.dataset.expanded = '1';
        link.textContent = t('groups.posts.show_less', 'Show less');
    } else {
        el.textContent = el.dataset.preview;
        link.dataset.expanded = '0';
        link.textContent = t('groups.posts.show_more', 'Show more');
    }
}

function deleteGroupPost(groupId, postId, btn) {
    btn.disabled = true;
    btn.querySelector('.msi').textContent = 'hourglass_empty';
    const card = btn.closest('.fd-group-card');
    if (card) { card.style.opacity = '.4'; card.style.pointerEvents = 'none'; }
    sendToCS({ action: 'vrcDeleteGroupPost', groupId, postId });
}

function deleteGroupEvent(groupId, eventId, btn) {
    btn.disabled = true;
    btn.querySelector('.msi').textContent = 'hourglass_empty';
    const card = btn.closest('.fd-group-card');
    if (card) { card.style.opacity = '.4'; card.style.pointerEvents = 'none'; }
    sendToCS({ action: 'vrcDeleteGroupEvent', groupId, eventId });
}

/* === Group Post Modal === */
let _groupPostGroupId = null;
let _groupPostImageBase64 = null;
let _groupPostSelectedFileId = null; // file_xxx from library picker

function renderGroupImageUploadTile(onclickHandler) {
    return `<div style="width:100%;aspect-ratio:1;border-radius:6px;cursor:pointer;background:var(--bg-input);border:1.5px dashed var(--border);display:flex;align-items:center;justify-content:center;flex-shrink:0;" onmouseover="this.style.borderColor='var(--accent)'" onmouseout="this.style.borderColor='var(--border)'" onclick="${onclickHandler}()" title="${esc(t('groups.common.upload_new_photo', 'Upload new photo'))}"><span class="msi" style="font-size:22px;color:var(--tx3);pointer-events:none;">add_photo_alternate</span></div>`;
}

function openGroupPostModal(groupId) {
    _groupPostGroupId = groupId;
    _groupPostImageBase64 = null;
    _groupPostSelectedFileId = null;

    let overlay = document.getElementById('groupPostOverlay');
    if (!overlay) {
        overlay = document.createElement('div');
        overlay.id = 'groupPostOverlay';
        overlay.style.cssText = 'position:fixed;inset:0;z-index:10002;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;';
        document.body.appendChild(overlay);
    }
    overlay.innerHTML = `
    <div class="gp-modal" role="dialog" aria-label="${esc(t('groups.posts.modal.aria_label', 'Create Group Post'))}">
        <div class="gp-modal-header">
            <span class="msi" style="font-size:20px;color:var(--accent);">edit</span>
            <span>${t('groups.posts.modal.title', 'Create Group Post')}</span>
        </div>
        <div class="gp-modal-body">
            <label class="gp-label">${t('groups.posts.fields.title', 'Title')}</label>
            <input id="gpTitle" class="vrcn-edit-field" type="text" placeholder="${esc(t('groups.posts.fields.title_placeholder', 'Post title...'))}" maxlength="200" style="width:100%;">
            <label class="gp-label" style="margin-top:12px;">${t('groups.posts.fields.content', 'Content')}</label>
            <textarea id="gpText" class="gp-textarea" placeholder="${esc(t('groups.posts.fields.content_placeholder', "What's on your mind?"))}" rows="5" maxlength="2000"></textarea>
            <div style="display:flex;gap:12px;margin-top:12px;flex-wrap:wrap;">
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">${t('groups.posts.fields.visibility', 'Visibility')}</label>
                    <select id="gpVisibility" class="wd-create-select" style="width:100%">
                        <option value="group">${t('groups.common.group_only', 'Group only')}</option>
                        <option value="public">${t('groups.common.public', 'Public')}</option>
                    </select>
                </div>
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">${t('groups.posts.fields.notification', 'Notification')}</label>
                    <select id="gpNotify" class="wd-create-select" style="width:100%">
                        <option value="0">${t('groups.common.no_notification', 'No notification')}</option>
                        <option value="1">${t('groups.common.send_notification', 'Send notification')}</option>
                    </select>
                </div>
            </div>
            <label class="gp-label" style="margin-top:12px;">${t('groups.posts.fields.image', 'Image')} <span style="color:var(--tx3);font-weight:400;">(${t('groups.common.optional', 'optional')})</span></label>
            <div id="gpImgGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(72px,1fr));gap:6px;max-height:180px;overflow-y:auto;padding:4px 0;"></div>
            <div id="gpError" style="display:none;margin-top:8px;padding:8px 10px;background:rgba(255,80,80,.12);border-radius:8px;color:var(--err);font-size:12px;"></div>
        </div>
        <div class="gp-modal-footer">
            <button class="vrcn-button-round vrcn-btn-join" id="gpSubmitBtn" onclick="submitGroupPost()"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">send</span>${t('groups.posts.submit', 'Post')}</button>
            <button class="vrcn-button-round" onclick="closeGroupPostModal()" style="margin-left:auto;">${t('common.cancel', 'Cancel')}</button>
        </div>
    </div>`;
    initAllVnSelects();
    overlay.style.display = 'flex';
    setTimeout(() => document.getElementById('gpTitle')?.focus(), 50);
    const _gpCached = (typeof invFilesCache !== 'undefined') && invFilesCache['gallery'];
    if (_gpCached && _gpCached.length > 0) renderGpImgGrid(_gpCached);
    else sendToCS({ action: 'invGetFiles', tag: 'gallery' });
}

function renderGpImgGrid(files) {
    const grid = document.getElementById('gpImgGrid');
    if (!grid) return;
    const plusTile = renderGroupImageUploadTile('gpOpenUpload');
    if (!files || !files.length) { grid.innerHTML = plusTile; return; }
    grid.innerHTML = plusTile + files.map(f => {
        const url = f.fileUrl || '';
        if (!url) return '';
        const fid = jsq(f.id || '');
        const fname = esc(f.name || f.id || '');
        return `<img src="${esc(url)}" title="${fname}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:0.85;transition:opacity .15s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=0.85" onclick="gpSelectLibraryPhoto('${fid}','${jsq(url)}')" onerror="this.style.display='none'">`;
    }).join('');
}

function gpOpenUpload() {
    openInvUploadModal('photos', file => {
        _groupPostSelectedFileId = file.id;
        _groupPostImageBase64 = null;
        renderGpImgGrid(invFilesCache['gallery'] || []);
        const first = document.querySelector('#gpImgGrid img');
        if (first) { document.querySelectorAll('#gpImgGrid img').forEach(el => el.style.outline = 'none'); first.style.outline = '2px solid var(--accent)'; }
    });
}

function gpSelectLibraryPhoto(fileId, url) {
    _groupPostSelectedFileId = fileId;
    _groupPostImageBase64 = null;
    document.querySelectorAll('#gpImgGrid img').forEach(el => el.style.outline = 'none');
    event.target.style.outline = '2px solid var(--accent)';
}

function onGroupPostGalleryLoaded(files) {
    if (!document.getElementById('gpImgGrid')) return;
    renderGpImgGrid(files);
}

function closeGroupPostModal() {
    const overlay = document.getElementById('groupPostOverlay');
    if (overlay) overlay.style.display = 'none';
    _groupPostGroupId = null;
    _groupPostImageBase64 = null;
    _groupPostSelectedFileId = null;
}


function submitGroupPost() {
    if (!_groupPostGroupId) return;
    const title = document.getElementById('gpTitle')?.value.trim() || '';
    const text = document.getElementById('gpText')?.value.trim() || '';
    const visibility = document.getElementById('gpVisibility')?.value || 'group';
    const sendNotification = document.getElementById('gpNotify')?.value === '1';
    const errEl = document.getElementById('gpError');

    if (!title) { if (errEl) { errEl.textContent = t('groups.posts.validation.title_required', 'Title is required.'); errEl.style.display = ''; } return; }
    if (!text) { if (errEl) { errEl.textContent = t('groups.posts.validation.content_required', 'Content is required.'); errEl.style.display = ''; } return; }
    if (errEl) errEl.style.display = 'none';

    const btn = document.getElementById('gpSubmitBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">hourglass_empty</span>${t('groups.posts.submitting', 'Posting...')}`; }

    const payload = {
        action: 'vrcCreateGroupPost',
        groupId: _groupPostGroupId,
        title,
        text,
        visibility,
        sendNotification,
    };
    if (_groupPostSelectedFileId) payload.imageFileId = _groupPostSelectedFileId;
    else if (_groupPostImageBase64) payload.imageBase64 = _groupPostImageBase64;

    sendToCS(payload);
    closeGroupPostModal();
}

/* === Group Event Date-Time Picker === */
let _gevDpTarget = null; // 'start' | 'end'
let _gevDpYear = 0, _gevDpMonth = 0;
let _gevDpSelDate = ''; // YYYY-MM-DD
let _gevDpHour = 12;   // 0-23 (internal always 24h)
let _gevDpMin  = 0;
let _gevDp24h  = true; // initialized to Windows 24h on first open

function _gevDpDateLocale() {
    return getLanguageLocale();
}

function _gevDpAmLabel() {
    return t('groups.date_picker.am', 'AM');
}

function _gevDpPmLabel() {
    return t('groups.date_picker.pm', 'PM');
}

function _gevDpWeekdaysHtml() {
    const fmt = new Intl.DateTimeFormat(_gevDpDateLocale(), { weekday: 'short' });
    return Array.from({ length: 7 }, (_, index) => fmt.format(new Date(2024, 0, 7 + index)))
        .map(label => `<div class="tl-dp-wd">${esc(label)}</div>`)
        .join('');
}

function _gevDpMonthLabel(year, month) {
    return new Intl.DateTimeFormat(_gevDpDateLocale(), { month: 'long', year: 'numeric' }).format(new Date(year, month, 1));
}

function _ensureGevDp() {
    const existing = document.getElementById('gevDatePicker');
    if (existing) {
        const weekdays = existing.querySelector('.tl-dp-weekdays');
        if (weekdays) weekdays.innerHTML = _gevDpWeekdaysHtml();
        const nowBtn = existing.querySelector('[data-gev-now]');
        if (nowBtn) nowBtn.textContent = t('groups.date_picker.now', 'Now');
        const okBtn = existing.querySelector('[data-gev-confirm]');
        if (okBtn) okBtn.textContent = t('groups.date_picker.confirm', 'OK');
        return;
    }
    const el = document.createElement('div');
    el.id = 'gevDatePicker';
    el.className = 'tl-date-picker';
    el.style.cssText = 'display:none;width:268px;z-index:10100;';
    el.innerHTML = `
        <div class="tl-dp-header">
            <button class="tl-dp-nav" onclick="gevDpNavMonth(-1)"><span class="msi" style="font-size:16px;">chevron_left</span></button>
            <span id="gevDpMonthLabel" class="tl-dp-month-label"></span>
            <button class="tl-dp-nav" onclick="gevDpNavMonth(1)"><span class="msi" style="font-size:16px;">chevron_right</span></button>
        </div>
        <div class="tl-dp-weekdays">${_gevDpWeekdaysHtml()}</div>
        <div id="gevDpDaysGrid" class="tl-dp-days"></div>
        <div style="display:flex;align-items:center;gap:6px;margin-top:10px;padding-top:10px;border-top:1px solid var(--brd);">
            <span class="msi" style="font-size:15px;color:var(--tx3);">schedule</span>
            <input id="gevDpHourInput" class="gev-dp-time-input" type="number" min="1" max="12" oninput="gevDpTimeChanged()">
            <span style="color:var(--tx2);font-weight:700;font-size:15px;">:</span>
            <input id="gevDpMinInput" class="gev-dp-time-input" type="number" min="0" max="59" oninput="gevDpTimeChanged()">
            <button id="gevDpAmPmBtn" class="gev-dp-ampm-btn" onclick="gevDpToggleAmPm()">${t('groups.date_picker.am', 'AM')}</button>
            <button id="gevDp24hBtn" class="gev-dp-24h-btn" onclick="gevDpToggle24h()">24h</button>
        </div>
        <div class="tl-dp-footer">
            <button class="vrcn-button-round" style="flex:1;justify-content:center;" onclick="gevDpNow()" data-gev-now>${t('groups.date_picker.now', 'Now')}</button>
            <button class="vrcn-button-round vrcn-btn-join" style="flex:1;justify-content:center;" onclick="gevDpConfirm()" data-gev-confirm>${t('groups.date_picker.confirm', 'OK')}</button>
        </div>`;
    document.body.appendChild(el);
}

function _gevDpFmtDate(year, month, day) {
    const d = new Date(year, month, day);
    return d.getFullYear() + '-' + String(d.getMonth()+1).padStart(2,'0') + '-' + String(d.getDate()).padStart(2,'0');
}


function openGevDp(target) {
    _ensureGevDp();
    _gevDpTarget = target;
    _gevDp24h = _dtIs24Hour;
    const existing = document.getElementById(target === 'start' ? 'gevStart' : 'gevEnd')?.value || '';
    if (existing) {
        const [datePart, timePart] = existing.split('T');
        _gevDpSelDate = datePart;
        if (timePart) { const [h, m] = timePart.split(':').map(Number); _gevDpHour = h||0; _gevDpMin = m||0; }
        const base = new Date(_gevDpSelDate + 'T00:00:00');
        _gevDpYear = base.getFullYear(); _gevDpMonth = base.getMonth();
    } else {
        const now = new Date();
        _gevDpSelDate = ''; _gevDpHour = now.getHours(); _gevDpMin = 0;
        _gevDpYear = now.getFullYear(); _gevDpMonth = now.getMonth();
    }
    _gevDpSyncTimeInputs();
    renderGevDpCalendar();
    const picker = document.getElementById('gevDatePicker');
    picker.style.display = '';
    const trigEl = document.getElementById(target === 'start' ? 'gevStartDisplay' : 'gevEndDisplay');
    if (trigEl) {
        const rect = trigEl.getBoundingClientRect();
        const ph = picker.offsetHeight || 360;
        const top = rect.bottom + 6 + ph > window.innerHeight ? rect.top - ph - 6 : rect.bottom + 6;
        picker.style.top  = Math.max(6, top) + 'px';
        picker.style.left = Math.min(rect.left, window.innerWidth - 276) + 'px';
    }
    setTimeout(() => document.addEventListener('click', _gevDpOutside), 0);
}

function _gevDpOutside(e) {
    const picker = document.getElementById('gevDatePicker');
    if (!picker) return;
    // Detached target = calendar grid was re-rendered (day click) — not an outside click
    if (!e.target.isConnected) {
        setTimeout(() => document.addEventListener('click', _gevDpOutside), 0);
        return;
    }
    const trigEl = document.getElementById(_gevDpTarget === 'start' ? 'gevStartDisplay' : 'gevEndDisplay');
    if (!picker.contains(e.target) && (!trigEl || !trigEl.contains(e.target))) {
        picker.style.display = 'none';
        document.removeEventListener('click', _gevDpOutside);
    } else {
        setTimeout(() => document.addEventListener('click', _gevDpOutside), 0);
    }
}

function gevDpNavMonth(dir) {
    _gevDpMonth += dir;
    if (_gevDpMonth < 0)  { _gevDpMonth = 11; _gevDpYear--; }
    if (_gevDpMonth > 11) { _gevDpMonth = 0;  _gevDpYear++; }
    renderGevDpCalendar();
}

function gevDpSelectDate(ds) { _gevDpSelDate = ds; renderGevDpCalendar(); }

function gevDpToggle24h() {
    gevDpTimeChanged();
    _gevDp24h = !_gevDp24h;
    _gevDpSyncTimeInputs();
}

function _gevDpFormatDisplay(dateStr, hour, min, use24) {
    const d = new Date(dateStr + 'T00:00:00');
    d.setHours(hour, min, 0, 0);
    return new Intl.DateTimeFormat(use24 ? _gevDpDateLocale() : _gevDpTimeLocale(), {
        weekday: 'short',
        month: 'short',
        day: 'numeric',
        hour: 'numeric',
        minute: '2-digit',
        hour12: !use24,
    }).format(d);
}

function _gevDpSyncTimeInputs() {
    const hInput  = document.getElementById('gevDpHourInput');
    const mInput  = document.getElementById('gevDpMinInput');
    const ampmBtn = document.getElementById('gevDpAmPmBtn');
    const h24Btn  = document.getElementById('gevDp24hBtn');
    if (!hInput) return;
    if (_gevDp24h) {
        hInput.min = '0';
        hInput.max = '23';
        hInput.value = String(_gevDpHour).padStart(2, '0');
        if (ampmBtn) ampmBtn.style.display = 'none';
        if (h24Btn) h24Btn.classList.add('active');
    } else {
        hInput.min = '1';
        hInput.max = '12';
        let h12 = _gevDpHour % 12;
        if (h12 === 0) h12 = 12;
        hInput.value = h12;
        if (ampmBtn) {
            ampmBtn.textContent = _gevDpHour < 12 ? _gevDpAmLabel() : _gevDpPmLabel();
            ampmBtn.style.display = '';
        }
        if (h24Btn) h24Btn.classList.remove('active');
    }
    if (mInput) mInput.value = String(_gevDpMin).padStart(2, '0');
}

function renderGevDpCalendar() {
    const label = document.getElementById('gevDpMonthLabel');
    const grid = document.getElementById('gevDpDaysGrid');
    if (!label || !grid) return;
    label.textContent = _gevDpMonthLabel(_gevDpYear, _gevDpMonth);
    const today = new Date();
    const todayStr = _gevDpFmtDate(today.getFullYear(), today.getMonth(), today.getDate());
    const firstDow = new Date(_gevDpYear, _gevDpMonth, 1).getDay();
    const daysInMonth = new Date(_gevDpYear, _gevDpMonth + 1, 0).getDate();
    const daysInPrev = new Date(_gevDpYear, _gevDpMonth, 0).getDate();
    let html = '';
    for (let i = firstDow - 1; i >= 0; i--) {
        const d = daysInPrev - i;
        const ds = _gevDpFmtDate(_gevDpYear, _gevDpMonth - 1, d);
        html += `<button class="tl-dp-day other-month${ds === _gevDpSelDate ? ' selected' : ''}" onclick="gevDpSelectDate('${ds}')">${d}</button>`;
    }
    for (let d = 1; d <= daysInMonth; d++) {
        const ds = _gevDpFmtDate(_gevDpYear, _gevDpMonth, d);
        const cls = (ds === todayStr ? ' today' : '') + (ds === _gevDpSelDate ? ' selected' : '');
        html += `<button class="tl-dp-day${cls}" onclick="gevDpSelectDate('${ds}')">${d}</button>`;
    }
    const used = firstDow + daysInMonth;
    const remaining = used % 7 === 0 ? 0 : 7 - (used % 7);
    for (let d = 1; d <= remaining; d++) {
        const ds = _gevDpFmtDate(_gevDpYear, _gevDpMonth + 1, d);
        html += `<button class="tl-dp-day other-month${ds === _gevDpSelDate ? ' selected' : ''}" onclick="gevDpSelectDate('${ds}')">${d}</button>`;
    }
    grid.innerHTML = html;
}

function gevDpTimeChanged() {
    const hInput = document.getElementById('gevDpHourInput');
    const mInput = document.getElementById('gevDpMinInput');
    if (!hInput) return;
    let h = parseInt(hInput.value) || 0;
    const m = Math.max(0, Math.min(59, parseInt(mInput.value) || 0));
    if (_gevDp24h) {
        _gevDpHour = Math.max(0, Math.min(23, h));
    } else {
        h = Math.max(1, Math.min(12, h));
        const isAm = document.getElementById('gevDpAmPmBtn')?.textContent === _gevDpAmLabel();
        _gevDpHour = (h === 12 ? 0 : h) + (isAm ? 0 : 12);
    }
    _gevDpMin = m;
}

function gevDpToggleAmPm() {
    const btn = document.getElementById('gevDpAmPmBtn');
    if (!btn) return;
    if (_gevDpHour < 12) {
        _gevDpHour += 12;
        btn.textContent = _gevDpPmLabel();
    } else {
        _gevDpHour -= 12;
        btn.textContent = _gevDpAmLabel();
    }
}

function gevDpNow() {
    const now = new Date();
    _gevDpSelDate = _gevDpFmtDate(now.getFullYear(), now.getMonth(), now.getDate());
    _gevDpHour = now.getHours(); _gevDpMin = now.getMinutes();
    _gevDpYear = now.getFullYear(); _gevDpMonth = now.getMonth();
    _gevDpSyncTimeInputs();
    renderGevDpCalendar();
}

function gevDpConfirm() {
    if (!_gevDpSelDate) return;
    gevDpTimeChanged();
    const value     = `${_gevDpSelDate}T${String(_gevDpHour).padStart(2,'0')}:${String(_gevDpMin).padStart(2,'0')}`;
    const hiddenId  = _gevDpTarget === 'start' ? 'gevStart' : 'gevEnd';
    const displayId = _gevDpTarget === 'start' ? 'gevStartDisplay' : 'gevEndDisplay';
    const hidden  = document.getElementById(hiddenId);
    const display = document.getElementById(displayId);
    if (hidden)  hidden.value = value;
    if (display) display.textContent = _gevDpFormatDisplay(_gevDpSelDate, _gevDpHour, _gevDpMin, _gevDp24h);
    document.getElementById('gevDatePicker').style.display = 'none';
    document.removeEventListener('click', _gevDpOutside);
}

function _gevDpSetDisplay(target, dtLocalStr) {
    if (!dtLocalStr) return;
    const [datePart, timePart] = dtLocalStr.split('T');
    const [h, m] = (timePart || '00:00').split(':').map(Number);
    document.getElementById(target === 'start' ? 'gevStart' : 'gevEnd').value = dtLocalStr;
    const display = document.getElementById(target === 'start' ? 'gevStartDisplay' : 'gevEndDisplay');
    if (display) display.textContent = _gevDpFormatDisplay(datePart, h, m, _gevDp24h);
}

/* === Group Event Modal === */
let _groupEventGroupId = null;
let _groupEventImageBase64 = null;
let _groupEventSelectedFileId = null;

function openGroupEventModal(groupId) {
    _groupEventGroupId = groupId;
    _groupEventImageBase64 = null;
    _groupEventSelectedFileId = null;

    // Default start: now + 1h, rounded to next full hour
    const now = new Date();
    now.setMinutes(0, 0, 0);
    now.setHours(now.getHours() + 1);
    const pad = n => String(n).padStart(2, '0');
    const localDT = v => `${v.getFullYear()}-${pad(v.getMonth()+1)}-${pad(v.getDate())}T${pad(v.getHours())}:${pad(v.getMinutes())}`;
    const defaultStart = localDT(now);
    const endD = new Date(now); endD.setHours(endD.getHours() + 1);
    const defaultEnd = localDT(endD);

    let overlay = document.getElementById('groupEventOverlay');
    if (!overlay) {
        overlay = document.createElement('div');
        overlay.id = 'groupEventOverlay';
        overlay.style.cssText = 'position:fixed;inset:0;z-index:10002;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;';
        document.body.appendChild(overlay);
    }
    overlay.innerHTML = `
    <div class="gp-modal" role="dialog" aria-label="${esc(t('groups.events.modal.aria_label', 'Create Group Event'))}" style="max-height:calc(100vh - 32px);overflow-y:auto;">
        <div class="gp-modal-header">
            <span class="msi" style="font-size:20px;color:var(--accent);">event</span>
            <span>${t('groups.events.modal.title', 'Create Group Event')}</span>
        </div>
        <div class="gp-modal-body">
            <label class="gp-label">${t('groups.events.fields.name', 'Event Name')}</label>
            <input id="gevName" class="vrcn-edit-field" type="text" placeholder="${esc(t('groups.events.fields.name_placeholder', 'Event name...'))}" maxlength="64" style="width:100%;">

            <label class="gp-label" style="margin-top:12px;">${t('groups.events.fields.description', 'Description')}</label>
            <textarea id="gevDesc" class="gp-textarea" placeholder="${esc(t('groups.events.fields.description_placeholder', "What's happening?"))}" rows="4" maxlength="2000"></textarea>

            <div style="display:flex;gap:12px;margin-top:12px;flex-wrap:wrap;">
                <div style="flex:1;min-width:160px;">
                    <label class="gp-label">${t('groups.events.fields.start', 'Start')}</label>
                    <div class="gp-input gev-dt-trigger" id="gevStartDisplay" onclick="openGevDp('start')"></div>
                    <input type="hidden" id="gevStart">
                </div>
                <div style="flex:1;min-width:160px;">
                    <label class="gp-label">${t('groups.events.fields.end', 'End')}</label>
                    <div class="gp-input gev-dt-trigger" id="gevEndDisplay" onclick="openGevDp('end')"></div>
                    <input type="hidden" id="gevEnd">
                </div>
            </div>

            <div style="display:flex;gap:12px;margin-top:12px;flex-wrap:wrap;">
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">${t('groups.events.fields.category', 'Category')}</label>
                    <select id="gevCategory" class="wd-create-select" style="width:100%">
                        <option value="hangout">${t('groups.events.categories.hangout', 'Hangout')}</option>
                        <option value="gaming">${t('groups.events.categories.gaming', 'Gaming')}</option>
                        <option value="music">${t('groups.events.categories.music', 'Music')}</option>
                        <option value="dance">${t('groups.events.categories.dance', 'Dance')}</option>
                        <option value="performance">${t('groups.events.categories.performance', 'Performance')}</option>
                        <option value="arts">${t('groups.events.categories.arts', 'Arts')}</option>
                        <option value="education">${t('groups.events.categories.education', 'Education')}</option>
                        <option value="exploration">${t('groups.events.categories.exploration', 'Exploration')}</option>
                        <option value="film_media">${t('groups.events.categories.film_media', 'Film & Media')}</option>
                        <option value="roleplaying">${t('groups.events.categories.roleplaying', 'Roleplaying')}</option>
                        <option value="wellness">${t('groups.events.categories.wellness', 'Wellness')}</option>
                        <option value="avatars">${t('groups.events.categories.avatars', 'Avatars')}</option>
                        <option value="other">${t('groups.events.categories.other', 'Other')}</option>
                    </select>
                </div>
                <div style="flex:1;min-width:130px;">
                    <label class="gp-label">${t('groups.events.fields.access_type', 'Access Type')}</label>
                    <select id="gevAccess" class="wd-create-select" style="width:100%">
                        <option value="group">${t('groups.common.group_only', 'Group only')}</option>
                        <option value="public">${t('groups.common.public', 'Public')}</option>
                    </select>
                </div>
            </div>

            <div style="margin-top:12px;">
                <label class="gp-label">${t('groups.events.fields.notification', 'Notification')}</label>
                <select id="gevNotify" class="wd-create-select" style="width:100%">
                    <option value="0">${t('groups.common.no_notification', 'No notification')}</option>
                    <option value="1">${t('groups.common.send_notification', 'Send notification')}</option>
                </select>
            </div>

            <label class="gp-label" style="margin-top:12px;">${t('groups.events.fields.image', 'Image')} <span style="color:var(--tx3);font-weight:400;">(${t('groups.common.optional', 'optional')})</span></label>
            <div id="gevImgGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(72px,1fr));gap:6px;max-height:180px;overflow-y:auto;padding:4px 0;"></div>

            <div id="gevError" style="display:none;margin-top:8px;padding:8px 10px;background:rgba(255,80,80,.12);border-radius:8px;color:var(--err);font-size:12px;"></div>
        </div>
        <div class="gp-modal-footer">
            <button class="vrcn-button-round vrcn-btn-join" id="gevSubmitBtn" onclick="submitGroupEvent()"><span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">event</span>${t('groups.events.submit', 'Create Event')}</button>
            <button class="vrcn-button-round" onclick="closeGroupEventModal()" style="margin-left:auto;">${t('common.cancel', 'Cancel')}</button>
        </div>
    </div>`;
    initAllVnSelects();
    _gevDpSetDisplay('start', defaultStart);
    _gevDpSetDisplay('end', defaultEnd);
    overlay.style.display = 'flex';
    setTimeout(() => document.getElementById('gevName')?.focus(), 50);
    const _gevCached = (typeof invFilesCache !== 'undefined') && invFilesCache['gallery'];
    if (_gevCached && _gevCached.length > 0) renderGevImgGrid(_gevCached);
    else sendToCS({ action: 'invGetFiles', tag: 'gallery' });
}

function renderGevImgGrid(files) {
    const grid = document.getElementById('gevImgGrid');
    if (!grid) return;
    const plusTile = renderGroupImageUploadTile('gevOpenUpload');
    if (!files || !files.length) { grid.innerHTML = plusTile; return; }
    grid.innerHTML = plusTile + files.map(f => {
        const url = f.fileUrl || '';
        if (!url) return '';
        const fid = jsq(f.id || '');
        const fname = esc(f.name || f.id || '');
        return `<img src="${esc(url)}" title="${fname}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:0.85;transition:opacity .15s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=0.85" onclick="gevSelectLibraryPhoto('${fid}','${jsq(url)}')" onerror="this.style.display='none'">`;
    }).join('');
}

function gevOpenUpload() {
    openInvUploadModal('photos', file => {
        _groupEventSelectedFileId = file.id;
        _groupEventImageBase64 = null;
        renderGevImgGrid(invFilesCache['gallery'] || []);
        const first = document.querySelector('#gevImgGrid img');
        if (first) { document.querySelectorAll('#gevImgGrid img').forEach(el => el.style.outline = 'none'); first.style.outline = '2px solid var(--accent)'; }
    });
}

function gevSelectLibraryPhoto(fileId, url) {
    _groupEventSelectedFileId = fileId;
    _groupEventImageBase64 = null;
    document.querySelectorAll('#gevImgGrid img').forEach(el => el.style.outline = 'none');
    event.target.style.outline = '2px solid var(--accent)';
}

function onGroupEventGalleryLoaded(files) {
    if (!document.getElementById('gevImgGrid')) return;
    renderGevImgGrid(files);
}

function closeGroupEventModal() {
    const overlay = document.getElementById('groupEventOverlay');
    if (overlay) overlay.style.display = 'none';
    _groupEventGroupId = null;
    _groupEventImageBase64 = null;
    _groupEventSelectedFileId = null;
}


function submitGroupEvent() {
    if (!_groupEventGroupId) return;
    const title = document.getElementById('gevName')?.value.trim() || '';
    const description = document.getElementById('gevDesc')?.value.trim() || '';
    const startVal = document.getElementById('gevStart')?.value || '';
    const endVal = document.getElementById('gevEnd')?.value || '';
    const category = document.getElementById('gevCategory')?.value || 'other';
    const accessType = document.getElementById('gevAccess')?.value || 'group';
    const sendCreationNotification = document.getElementById('gevNotify')?.value === '1';
    const errEl = document.getElementById('gevError');

    if (!title) { if (errEl) { errEl.textContent = t('groups.events.validation.name_required', 'Event name is required.'); errEl.style.display = ''; } return; }
    if (!description) { if (errEl) { errEl.textContent = t('groups.events.validation.description_required', 'Description is required.'); errEl.style.display = ''; } return; }
    if (!startVal) { if (errEl) { errEl.textContent = t('groups.events.validation.start_required', 'Start date/time is required.'); errEl.style.display = ''; } return; }
    if (!endVal) { if (errEl) { errEl.textContent = t('groups.events.validation.end_required', 'End date/time is required.'); errEl.style.display = ''; } return; }
    if (new Date(endVal) <= new Date(startVal)) { if (errEl) { errEl.textContent = t('groups.events.validation.end_after_start', 'End must be after start.'); errEl.style.display = ''; } return; }
    if (errEl) errEl.style.display = 'none';

    const btn = document.getElementById('gevSubmitBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">hourglass_empty</span>${t('groups.events.submitting', 'Creating...')}`; }

    const payload = {
        action: 'vrcCreateGroupEvent',
        groupId: _groupEventGroupId,
        title,
        description,
        startsAt: new Date(startVal).toISOString(),
        endsAt: new Date(endVal).toISOString(),
        category,
        accessType,
        sendCreationNotification,
    };
    if (_groupEventSelectedFileId) payload.imageFileId = _groupEventSelectedFileId;
    else if (_groupEventImageBase64) payload.imageBase64 = _groupEventImageBase64;

    sendToCS(payload);
    closeGroupEventModal();
}

/* ============================================================
   GROUP ROLES
   ============================================================ */

const ROLE_PERM_DEFS = [
    { key: 'group-members-manage',               label: 'Manage Group Member Data',            desc: 'View, filter, sort, and edit data about all members.' },
    { key: 'group-data-manage',                  label: 'Manage Group Data',                   desc: 'Edit group details (name, description, joinState, etc).' },
    { key: 'group-audit-view',                   label: 'View Audit Log',                      desc: 'View the full group audit log.' },
    { key: 'group-roles-manage',                 label: 'Manage Group Roles',                  desc: 'Create, modify, and delete roles.' },
    { key: 'group-default-role-manage',          label: 'Manage Default Role',                 desc: 'Manage permissions for the default (Everyone) role.' },
    { key: 'group-roles-assign',                 label: 'Assign Group Roles',                  desc: 'Assign/unassign roles to users. Requires "Manage Group Member Data".' },
    { key: 'group-bans-manage',                  label: 'Manage Group Bans',                   desc: 'Ban/unban users and view all banned users. Requires "Manage Group Member Data".' },
    { key: 'group-members-remove',               label: 'Remove Group Members',                desc: 'Remove someone from the group. Requires "Manage Group Member Data".' },
    { key: 'group-members-viewall',              label: 'View All Members',                    desc: 'View all members in the group, not just friends.' },
    { key: 'group-announcement-manage',          label: 'Manage Group Announcement',           desc: 'Set/clear group announcement and send it as a notification.' },
    { key: 'group-calendar-manage',              label: 'Manage Group Calendar',               desc: 'Create, modify, and publish calendar entries.' },
    { key: 'group-galleries-manage',             label: 'Manage Group Galleries',              desc: 'Create, reorder, edit, and delete group galleries.' },
    { key: 'group-invites-manage',               label: 'Manage Group Invites',                desc: 'Create/cancel invites, accept/decline/block join requests.' },
    { key: 'group-instance-moderate',            label: 'Moderate Group Instances',            desc: 'Moderate within a group instance.' },
    { key: 'group-instance-manage',              label: 'Manage Group Instances',              desc: 'Rename or close a group instance.' },
    { key: 'group-instance-queue-priority',      label: 'Group Instance Queue Priority',       desc: 'Priority for group instance queues.' },
    { key: 'group-instance-age-gated-create',    label: 'Create Age Gated Instances',          desc: 'Create instances requiring age verification (18+).' },
    { key: 'group-instance-public-create',       label: 'Create Public Group Instances',       desc: 'Create instances open to all, member or not.' },
    { key: 'group-instance-plus-create',         label: 'Create Group+ Instances',             desc: 'Create instances that friends of attendees can also join.' },
    { key: 'group-instance-open-create',         label: 'Create Open Group Instances',         desc: 'Create open group instances.' },
    { key: 'group-instance-restricted-create',   label: 'Create Role-Restricted Instances',    desc: 'Create instances restricted to specific roles.' },
    { key: 'group-instance-plus-portal',         label: 'Portal to Group+ Instances',          desc: 'Open locked portals to Group+ instances.' },
    { key: 'group-instance-plus-portal-unlocked',label: 'Unlocked Portal to Group+ Instances', desc: 'Open unlocked portals to Group+ instances.' },
    { key: 'group-instance-join',                label: 'Join Group Instances',                desc: 'Join group instances.' },
];

function getRolePermissionLabel(def) {
    return t(`groups.roles.permissions.${def.key}.label`, def.label);
}

function getRolePermissionDescription(def) {
    return t(`groups.roles.permissions.${def.key}.desc`, def.desc);
}

function getRoleMetaText(role) {
    const parts = [];
    if ((role.permissions || []).includes('*')) {
        parts.push(`* (${t('groups.roles.meta.all', 'all')})`);
    } else {
        const count = (role.permissions || []).length;
        parts.push(`${count} ${t(count === 1 ? 'groups.roles.meta.permission_one' : 'groups.roles.meta.permission_other', count === 1 ? 'permission' : 'permissions')}`);
    }
    if (role.isAddedOnJoin) parts.push(t('groups.roles.meta.auto_join', 'Auto-join'));
    if (role.isSelfAssignable) parts.push(t('groups.roles.meta.self_assign', 'Self-assign'));
    return parts.join(' - ');
}

function _buildRoleEditor(role, groupId, canManage, isSystemRole) {
    const rid = esc(role.id);
    const gid = jsq(groupId);
    const isOwner = (role.permissions || []).includes('*');
    const dis = (canManage && !isOwner) ? '' : 'disabled';
    const generalHtml = `
    <div class="gd-role-section">
        <div class="gd-role-section-title">${t('groups.roles.general', 'General')}</div>
        <div class="sf-row" style="margin-bottom:8px;">
            <label style="font-size:12px;color:var(--tx2);min-width:50px;">${t('groups.roles.name', 'Name')}</label>
            <input id="grole-name-${rid}" class="vrcn-edit-field" value="${esc(role.name)}" maxlength="64" style="flex:1;" ${dis}>
        </div>
        <div class="sf-toggle-row"><span>${t('groups.roles.assign_on_join', 'Assign On Join')}</span>
            <label class="toggle"><input type="checkbox" id="grole-join-${rid}" ${role.isAddedOnJoin ? 'checked' : ''} ${dis}><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
        <div class="sf-toggle-row"><span>${t('groups.roles.self_assignable', 'Self Assignable')}</span>
            <label class="toggle"><input type="checkbox" id="grole-self-${rid}" ${role.isSelfAssignable ? 'checked' : ''} ${dis}><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
        <div class="sf-toggle-row"><span>${t('groups.roles.require_2fa', 'Require Two Factor Authentication')}</span>
            <label class="toggle"><input type="checkbox" id="grole-tfa-${rid}" ${role.requiresTwoFactor ? 'checked' : ''} ${dis}><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
    </div>`;
    const hasWildcard = (role.permissions || []).includes('*');
    const permsHtml = `
    <div class="gd-role-section">
        <div class="gd-role-section-title">${t('groups.roles.permissions', 'Permissions')}</div>
        ${hasWildcard
            ? `<div style="padding:8px 0;font-size:12px;color:var(--tx2);">${t('groups.roles.full_access', 'This role has full access (all permissions).')}</div>`
            : ROLE_PERM_DEFS.map(p => {
                const checked = (role.permissions || []).includes(p.key);
                return `<div class="gd-perm-row">
                    <div class="gd-perm-info"><div class="gd-perm-label">${getRolePermissionLabel(p)}</div><div class="gd-perm-desc">${getRolePermissionDescription(p)}</div></div>
                    <label class="toggle" style="flex-shrink:0;margin-left:12px;"><input type="checkbox" data-perm-key="${p.key}" ${checked ? 'checked' : ''} ${dis}><div class="toggle-track"><div class="toggle-knob"></div></div></label>
                </div>`;
            }).join('')}
    </div>`;
    const canSave = canManage && !isOwner;
    const actionBtns = canSave ? `
    <div style="display:flex;gap:8px;margin-top:14px;justify-content:space-between;">
        ${!isSystemRole ? `<button class="vrcn-button-round vrcn-btn-danger" style="font-size:11px;" onclick="deleteGroupRole('${jsq(role.id)}','${gid}')"><span class="msi" style="font-size:14px;">delete</span> ${t('groups.roles.delete', 'Delete')}</button>` : '<div></div>'}
        <button class="vrcn-button-round vrcn-btn-join" style="font-size:11px;" onclick="saveGroupRole('${jsq(role.id)}','${gid}')"><span class="msi" style="font-size:14px;">save</span> ${t('groups.roles.save_changes', 'Save Changes')}</button>
    </div>` : '';
    return generalHtml + permsHtml + actionBtns;
}

function _buildRoleCard(role, groupId, canManage) {
    const rid = esc(role.id);
    const badge = role.isManagementRole ? `<span class="vrcn-badge" style="margin-right:6px;">${t('groups.roles.system', 'System')}</span>` : '';
    return `<div class="gd-role-card" id="gdrole-${rid}">
        <div class="gd-role-header" onclick="toggleGdRoleExpand('${rid}')">
            <div style="flex:1;"><div class="gd-role-name">${badge}${esc(role.name)}</div><div class="gd-role-meta">${getRoleMetaText(role)}</div></div>
            <span class="msi gd-role-chevron" style="font-size:18px;color:var(--tx3);transition:transform .2s;">expand_more</span>
        </div>
        <div class="gd-role-body" id="gdrole-body-${rid}" style="display:none;">
            <div class="fd-tabs gd-tabs" style="margin:10px 14px 0;">
                <button class="fd-tab active" onclick="switchGdRoleTab('${rid}','settings',this)">${t('groups.roles.tabs.settings', 'Settings')}</button>
                <button class="fd-tab" onclick="switchGdRoleTab('${rid}','members',this)">${t('groups.roles.tabs.members', 'Members')}</button>
            </div>
            <div id="gdrole-settings-${rid}">
                ${_buildRoleEditor(role, groupId, canManage, role.isManagementRole)}
            </div>
            <div id="gdrole-members-${rid}" style="display:none;">
                <div id="gdrole-members-list-${rid}" style="padding:8px 0;">
                    <div style="padding:16px;text-align:center;font-size:12px;color:var(--tx3);">${t('groups.roles.click_load_members', 'Click to load members...')}</div>
                </div>
            </div>
        </div>
    </div>`;
}

function _buildCreateRoleForm(groupId) {
    const gid = jsq(groupId);
    return `<div id="gdRoleCreateForm" style="display:none;margin-bottom:10px;">
        <div class="gd-role-card" style="border:1.5px dashed var(--accent);">
            <div style="padding:12px 14px 0;"><div class="gd-role-section-title">${t('groups.roles.new_role', 'New Role')}</div>
            <div class="sf-row" style="margin-bottom:8px;">
                <label style="font-size:12px;color:var(--tx2);min-width:50px;">${t('groups.roles.name', 'Name')}</label>
                <input id="gdNewRoleName" class="vrcn-edit-field" placeholder="${esc(t('groups.roles.name_placeholder', 'Role name...'))}" maxlength="64" style="flex:1;">
            </div>
            <div class="sf-toggle-row"><span>${t('groups.roles.assign_on_join', 'Assign On Join')}</span>
                <label class="toggle"><input type="checkbox" id="gdNewRoleJoin"><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
            <div class="sf-toggle-row"><span>${t('groups.roles.self_assignable', 'Self Assignable')}</span>
                <label class="toggle"><input type="checkbox" id="gdNewRoleSelf"><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
            <div class="sf-toggle-row"><span>${t('groups.roles.require_2fa', 'Require Two Factor Authentication')}</span>
                <label class="toggle"><input type="checkbox" id="gdNewRoleTfa"><div class="toggle-track"><div class="toggle-knob"></div></div></label></div>
            <div class="gd-role-section-title" style="margin-top:14px;">${t('groups.roles.permissions', 'Permissions')}</div>
            ${ROLE_PERM_DEFS.map(p => `<div class="gd-perm-row">
                <div class="gd-perm-info"><div class="gd-perm-label">${getRolePermissionLabel(p)}</div><div class="gd-perm-desc">${getRolePermissionDescription(p)}</div></div>
                <label class="toggle" style="flex-shrink:0;margin-left:12px;"><input type="checkbox" class="gdNewRolePerm" data-perm-key="${p.key}"><div class="toggle-track"><div class="toggle-knob"></div></div></label>
            </div>`).join('')}
            <div style="display:flex;gap:8px;margin-top:14px;padding-bottom:14px;">
                <button class="vrcn-button-round" style="font-size:11px;" onclick="closeCreateRoleForm()">${t('common.cancel', 'Cancel')}</button>
                <button class="vrcn-button-round vrcn-btn-join" style="font-size:11px;margin-left:auto;" onclick="submitCreateRole('${gid}')"><span class="msi" style="font-size:14px;">add</span> ${t('groups.roles.create_role', 'Create Role')}</button>
            </div></div>
        </div>
    </div>`;
}

function _buildRolesTab(g) {
    const canManage = g.canManageRoles === true;
    const roles = g.roles || [];
    const createBtn = `<button class="vrcn-button-round" style="margin-bottom:10px;" onclick="openCreateRoleForm()"><span class="msi" style="font-size:14px;">add</span> ${t('groups.roles.create_role', 'Create Role')}</button>`;
    const createForm = _buildCreateRoleForm(g.id);
    const roleCards = roles.length
        ? roles.map(r => _buildRoleCard(r, g.id, canManage)).join('')
        : `<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">${t('groups.roles.empty', 'No roles found')}</div>`;
    return `${createBtn}${createForm}<div id="gdRolesList">${roleCards}</div>`;
}

function toggleGdRoleExpand(roleId) {
    const body    = document.getElementById('gdrole-body-' + roleId);
    const chevron = document.querySelector('#gdrole-' + roleId + ' .gd-role-chevron');
    if (!body) return;
    const open = body.style.display !== 'none';
    body.style.display = open ? 'none' : '';
    if (chevron) chevron.style.transform = open ? '' : 'rotate(180deg)';
}

function switchGdRoleTab(roleId, tab, btn) {
    document.getElementById('gdrole-settings-' + roleId).style.display = tab === 'settings' ? '' : 'none';
    document.getElementById('gdrole-members-'  + roleId).style.display = tab === 'members'  ? '' : 'none';
    btn.closest('.fd-tabs').querySelectorAll('.fd-tab').forEach(b => b.classList.toggle('active', b === btn));
    if (tab === 'members') {
        const listEl = document.getElementById('gdrole-members-list-' + roleId);
        if (listEl && !listEl.dataset.loaded) {
            listEl.dataset.loaded = '1';
            listEl.innerHTML = renderGroupEmptyMessage('common.loading', 'Loading...');
            const g = window._currentGroupDetail;
            if (g) sendToCS({ action: 'vrcGetGroupRoleMembers', groupId: g.id, roleId });
        }
    }
}

function onGroupRoleMembers(data) {
    const listEl = document.getElementById('gdrole-members-list-' + data.roleId);
    if (!listEl) return;
    if (!data.members || data.members.length === 0) {
        listEl.innerHTML = renderGroupEmptyMessage('groups.roles.no_members', 'No members with this role.');
        return;
    }
    listEl.innerHTML = data.members.map(m => renderGroupMemberCard(m)).join('');
}

function openCreateRoleForm() {
    const form = document.getElementById('gdRoleCreateForm');
    if (form) { form.style.display = ''; form.querySelector('input')?.focus(); }
}

function closeCreateRoleForm() {
    const form = document.getElementById('gdRoleCreateForm');
    if (form) form.style.display = 'none';
}

function submitCreateRole(groupId) {
    const name = document.getElementById('gdNewRoleName')?.value.trim() || '';
    if (!name) { showToast(false, t('groups.roles.validation.name_required', 'Role name is required')); return; }
    const perms = Array.from(document.querySelectorAll('.gdNewRolePerm:checked')).map(el => el.dataset.permKey);
    sendToCS({
        action: 'vrcCreateGroupRole', groupId, name, description: '',
        permissions: perms,
        isAddedOnJoin:    document.getElementById('gdNewRoleJoin')?.checked || false,
        isSelfAssignable: document.getElementById('gdNewRoleSelf')?.checked || false,
        requiresTwoFactor:document.getElementById('gdNewRoleTfa')?.checked  || false,
    });
}

function saveGroupRole(roleId, groupId) {
    const name = document.getElementById('grole-name-' + roleId)?.value.trim() || '';
    if (!name) { showToast(false, t('groups.roles.validation.name_required', 'Role name is required')); return; }
    const perms = Array.from(document.querySelectorAll(`#gdrole-body-${roleId} [data-perm-key]:checked`)).map(el => el.dataset.permKey);
    sendToCS({
        action: 'vrcUpdateGroupRole', groupId, roleId, name, permissions: perms,
        isAddedOnJoin:    document.getElementById('grole-join-' + roleId)?.checked || false,
        isSelfAssignable: document.getElementById('grole-self-' + roleId)?.checked || false,
        requiresTwoFactor:document.getElementById('grole-tfa-'  + roleId)?.checked || false,
    });
}

function deleteGroupRole(roleId, groupId) {
    const card = document.getElementById('gdrole-' + roleId);
    if (card) { card.style.opacity = '0.4'; card.style.pointerEvents = 'none'; }
    sendToCS({ action: 'vrcDeleteGroupRole', groupId, roleId });
}

function onGroupRoleResult(payload) {
    if (!payload.success) {
        const msg = {
            create: t('groups.roles.result.create_failed', 'Failed to create role'),
            update: t('groups.roles.result.update_failed', 'Failed to save role'),
            delete: t('groups.roles.result.delete_failed', 'Failed to delete role'),
        }[payload.action] || t('groups.roles.result.action_failed', 'Role action failed');
        showToast(false, msg);
        if (payload.action === 'delete') {
            const card = document.getElementById('gdrole-' + payload.roleId);
            if (card) { card.style.opacity = ''; card.style.pointerEvents = ''; }
        }
        return;
    }
    if (payload.action === 'create') {
        showToast(true, t('groups.roles.result.created', 'Role created'));
        closeCreateRoleForm();
        if (payload.role && window._currentGroupDetail) {
            window._currentGroupDetail.roles = [...(window._currentGroupDetail.roles || []), payload.role];
            if (window._currentGroupDetailFull) {
                window._currentGroupDetailFull.roles = [...(window._currentGroupDetailFull.roles || []), payload.role];
            }
            const list = document.getElementById('gdRolesList');
            if (list) list.insertAdjacentHTML('beforeend', _buildRoleCard(payload.role, payload.groupId, true));
        }
    } else if (payload.action === 'update') {
        showToast(true, t('groups.roles.result.saved', 'Role saved'));
        const updatedRole = {
            id: payload.roleId,
            name: document.getElementById('grole-name-' + payload.roleId)?.value.trim() || '',
            permissions: Array.from(document.querySelectorAll(`#gdrole-body-${payload.roleId} [data-perm-key]:checked`)).map(el => el.dataset.permKey),
            isAddedOnJoin: document.getElementById('grole-join-' + payload.roleId)?.checked || false,
            isSelfAssignable: document.getElementById('grole-self-' + payload.roleId)?.checked || false,
            requiresTwoFactor: document.getElementById('grole-tfa-' + payload.roleId)?.checked || false,
        };
        [window._currentGroupDetail, window._currentGroupDetailFull].forEach(group => {
            if (!group?.roles) return;
            group.roles = group.roles.map(role => role.id === payload.roleId ? { ...role, ...updatedRole } : role);
        });
    } else if (payload.action === 'delete') {
        showToast(true, t('groups.roles.result.deleted', 'Role deleted'));
        if (window._currentGroupDetail)
            window._currentGroupDetail.roles = (window._currentGroupDetail.roles || []).filter(r => r.id !== payload.roleId);
        if (window._currentGroupDetailFull)
            window._currentGroupDetailFull.roles = (window._currentGroupDetailFull.roles || []).filter(r => r.id !== payload.roleId);
        document.getElementById('gdrole-' + payload.roleId)?.remove();
    }
}

/* ============================================================
   GROUP BANNED MEMBERS
   ============================================================ */

function _buildBannedTab() {
    return `<div id="gdBannedList">${renderGroupEmptyMessage('common.loading', 'Loading...')}</div>`;
}

function loadGroupBans() {
    if (!window._currentGroupDetail?.id) return;
    window._gdBannedLoaded = true;
    sendToCS({ action: 'vrcGetGroupBans', groupId: window._currentGroupDetail.id });
}

function renderGroupBans(groupId, bans) {
    const list = document.getElementById('gdBannedList');
    if (!list) return;
    if (!bans || bans.length === 0) {
        list.innerHTML = renderGroupEmptyMessage('groups.empty.no_banned_members', 'No banned members');
        return;
    }
    list.innerHTML = bans.map(b => renderProfileItem(b, `closeDetailModal();openFriendDetail('${jsq(b.id || '')}')`)).join('');
}

/* === Group Invite (reuses modalInvite / inviteBox) === */
let _grpInvGroupId = null;

function openGroupInviteModal(groupId) {
    _grpInvGroupId = groupId;
    const m = document.getElementById('modalInvite');
    if (!m) return;
    _inviteSelected = new Set();
    _inviteSending = false;
    _inviteFilter = '';
    _inviteProgressState = null;
    _inviteOverride = null;
    _renderGroupInviteBox();
    m.style.display = 'flex';
}

function _renderGroupInviteBox() {
    const box = document.getElementById('inviteBox');
    if (!box) return;
    const gd = window._currentGroupDetailFull || window._currentGroupDetail || {};
    const groupName = gd.name || '';
    const groupIcon = gd.iconUrl || '';
    const bannerUrl = gd.bannerUrl || '';
    const bannerBg = bannerUrl || groupIcon;
    box.innerHTML = `
        <div class="inv-world-banner" style="background-image:url('${esc(bannerBg)}')">
            <div class="inv-world-fade"></div>
            <div class="inv-world-info">
                <div class="inv-world-name">${esc(groupName)}</div>
                <div style="font-size:10px;color:rgba(255,255,255,.65);margin-top:3px;">${esc(t('groups.invite.subtitle', 'Invite to this group'))}</div>
            </div>
            <button class="inv-close-btn" onclick="closeInviteModal();_grpInvGroupId=null;" title="${esc(t('common.close', 'Close'))}"><span class="msi">close</span></button>
        </div>
        <div class="inv-search-wrap">
            <span class="msi inv-search-icon">search</span>
            <input type="text" id="inviteSearch" class="inv-search-input" placeholder="${esc(t('invite.multi.search_placeholder', 'Search friends...'))}" oninput="filterInviteList()">
        </div>
        <div id="inviteList" class="inv-list"></div>
        <div class="inv-footer">
            <span id="inviteSelCount" class="inv-sel-count"></span>
            <button id="inviteSendBtn" class="vrcn-button" onclick="_sendGroupInvites()" disabled>${esc(t('groups.actions.invite', 'Invite'))}</button>
        </div>
        <div id="inviteProgress" class="inv-progress-wrap" style="display:none;">
            <div class="inv-progress-track"><div id="inviteProgressBar" class="inv-progress-bar"></div></div>
            <div id="inviteProgressText" class="inv-progress-text"></div>
        </div>`;
    const search = document.getElementById('inviteSearch');
    if (search) search.value = _inviteFilter;
    renderInviteList(_inviteFilter);
}

function _sendGroupInvites() {
    const ids = Array.from(_inviteSelected);
    if (!ids.length || _inviteSending || !_grpInvGroupId) return;
    _inviteSending = true;
    const btn = document.getElementById('inviteSendBtn');
    if (btn) btn.disabled = true;
    const prog = document.getElementById('inviteProgress');
    if (prog) prog.style.display = '';
    _applyInviteProgress(0, ids.length, 0, 0);
    sendToCS({ action: 'vrcInviteToGroup', groupId: _grpInvGroupId, userIds: ids });
}

function handleGroupInviteProgress(payload) {
    _applyInviteProgress(payload.done, payload.total, payload.success, payload.fail);
    if (payload.error) showToast(false, payload.error);
    if (payload.done >= payload.total) {
        _inviteSending = false;
        _grpInvGroupId = null;
        setTimeout(() => {
            const count = _inviteSelected.size;
            const btn = document.getElementById('inviteSendBtn');
            if (btn) { btn.disabled = count === 0; }
        }, 1500);
    }
}
