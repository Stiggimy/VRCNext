/* === VRChat API === */
function vrcQuickLogin() {
    const u = document.getElementById('vrcQuickUser').value, p = document.getElementById('vrcQuickPass').value;
    if (!u || !p) return;
    document.getElementById('vrcQuickError').textContent = t('profiles.login.connecting', 'Connecting...');
    sendToCS({ action: 'vrcLogin', username: u, password: p });
}

function vrcLoginFromSettings() {
    const u = document.getElementById('setVrcUser').value, p = document.getElementById('setVrcPass').value;
    if (!u || !p) {
        document.getElementById('vrcLoginStatus').textContent = t('profiles.login.enter_credentials', 'Enter username and password');
        return;
    }
    document.getElementById('vrcLoginStatus').textContent = t('profiles.login.connecting', 'Connecting...');
    sendToCS({ action: 'vrcLogin', username: u, password: p });
}

function update2FAMessage(type = vrc2faType) {
    const msgEl = document.getElementById('modal2FAMsg');
    if (!msgEl) return;
    msgEl.textContent = type === 'emailotp'
        ? t('profiles.2fa.message_email', 'Enter the 6-digit code sent to your email.')
        : t('profiles.2fa.message_app', 'Enter the 6-digit code from your authenticator app.');
}

function show2FAModal(type) {
    vrc2faType = type;
    const m = document.getElementById('modal2FA');
    m.style.display = 'flex';
    document.getElementById('modal2FACode').value = '';
    document.getElementById('modal2FAError').textContent = '';
    update2FAMessage(type);
    setTimeout(() => document.getElementById('modal2FACode').focus(), 100);
}

function modal2FASubmit() {
    const c = document.getElementById('modal2FACode').value.trim();
    if (!c || c.length < 6) {
        document.getElementById('modal2FAError').textContent = t('profiles.2fa.enter_full_code', 'Enter the full 6-digit code');
        return;
    }
    document.getElementById('modal2FAError').textContent = t('profiles.2fa.verifying', 'Verifying...');
    sendToCS({ action: 'vrc2FA', code: c, type: vrc2faType });
}

function vrcLogout() {
    sendToCS({ action: 'vrcLogout' });
    document.getElementById('btnVrcLogin').style.display = '';
    document.getElementById('btnVrcLogout').style.display = 'none';
    document.getElementById('vrcLoginStatus').textContent = t('profiles.login.disconnected', 'Disconnected');
    currentVrcUser = null;
}

function vrcRefresh() {
    sendToCS({ action: 'vrcRefreshFriends' });
    requestInstanceInfo();
    refreshNotifications();
}

function closeDetailModal() { document.getElementById('modalDetail').style.display = 'none'; }

function statusDotClass(s) {
    if (!s) return 's-offline';
    const sl = s.toLowerCase();
    if (sl === 'active' || sl === 'online') return 's-active';
    if (sl === 'join me') return 's-join';
    if (sl === 'ask me' || sl === 'look me') return 's-ask';
    if (sl === 'busy' || sl === 'do not disturb') return 's-busy';
    return 's-offline';
}

function statusLabel(s) {
    if (!s) return t('status.offline', 'Offline');
    const sl = s.toLowerCase();
    const m = {
        'active': t('status.online', 'Online'),
        'online': t('status.online', 'Online'),
        'join me': t('status.join_me', 'Join Me'),
        'ask me': t('status.ask_me', 'Ask Me'),
        'look me': t('status.ask_me', 'Ask Me'),
        'busy': t('status.do_not_disturb', 'Do Not Disturb'),
        'do not disturb': t('status.do_not_disturb', 'Do Not Disturb'),
        'offline': t('status.offline', 'Offline')
    };
    return m[sl] || s;
}

function getFriendLocationLabel(presenceType, location) {
    const isPrivate = !location || location === 'private';
    const isOffline = location === 'offline' || presenceType === 'offline';
    if (isOffline) return t('status.offline', 'Offline');
    if (presenceType === 'web') return t('profiles.friends.location.web', 'Web / Mobile');
    if (isPrivate) return t('profiles.friends.location.private', 'Private Instance');
    const { worldId } = parseFriendLocation(location);
    const cached = worldId && (typeof dashWorldCache !== 'undefined') ? dashWorldCache[worldId] : null;
    return cached?.name || t('profiles.friends.location.world', 'In World');
}

function getFriendSectionLabel(section, count) {
    const map = {
        favorites: ['profiles.friends.sections.favorites', 'FAVORITES - {count}'],
        ingame: ['profiles.friends.sections.in_game', 'IN-GAME - {count}'],
        web: ['profiles.friends.sections.web', 'WEB / ACTIVE - {count}'],
        offline: ['profiles.friends.sections.offline', 'OFFLINE - {count}'],
        onlineZero: ['profiles.friends.sections.online_zero', 'ONLINE - {count}']
    };
    const entry = map[section];
    if (!entry) return `${count}`;
    return tf(entry[0], { count }, entry[1]);
}

function getFriendSectionShortLabel(section) {
    const map = {
        favorites: ['profiles.friends.sections_short.favorites', 'FAV'],
        ingame: ['profiles.friends.sections_short.in_game', 'GME'],
        web: ['profiles.friends.sections_short.web', 'WEB'],
        offline: ['profiles.friends.sections_short.offline', 'OFF']
    };
    const entry = map[section];
    return entry ? t(entry[0], entry[1]) : '';
}

function getProfileMutualBadgeLabel(count) {
    return count === 1
        ? tf('profiles.badges.mutual.one', { count }, '{count} Mutual')
        : tf('profiles.badges.mutual.other', { count }, '{count} Mutuals');
}

function getStatusText(status, description) {
    return `${statusLabel(status)}${description ? ' - ' + esc(description) : ''}`;
}

function getFriendStatusLine(friend) {
    if (!friend) {
        return `<span class="vrc-status-dot s-offline" style="width:6px;height:6px;flex-shrink:0;"></span><span class="fav-friend-status-text">${t('status.offline', 'Offline')}</span>`;
    }
    const dotCls = friend.presence === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
    return `<span class="${dotCls} ${statusDotClass(friend.status)}" style="width:6px;height:6px;flex-shrink:0;"></span><span class="fav-friend-status-text">${getStatusText(friend.status, friend.statusDescription)}</span>`;
}

function getGroupMemberText(memberCount, fallbackToGroup = true) {
    if (memberCount) return tf('worlds.groups.members', { count: memberCount }, '{count} members');
    return fallbackToGroup ? t('groups.common.group', 'Group') : '';
}

function renderVrcProfile(u) {
    const a = document.getElementById('vrcProfileArea');
    if (!u) { a.innerHTML = ''; currentVrcUser = null; return; }
    currentVrcUser = u;
    // If My Profile modal is open, refresh it immediately
    const _myp = document.getElementById('modalMyProfile');
    if (_myp && _myp.style.display !== 'none') renderMyProfileContent();
    const img = u.image || '';
    const imgTag = img
        ? `<img class="vrc-avatar" src="${img}" onerror="this.style.display='none'">`
        : `<div class="vrc-avatar" style="display:flex;align-items:center;justify-content:center;font-size:13px;font-weight:700;color:var(--tx3)">${esc((u.displayName || '?')[0])}</div>`;
    a.innerHTML = `<div class="vrc-profile" data-status="${statusDotClass(u.status)}" onclick="openMyProfileModal()">${imgTag}<div class="vrc-profile-info"><div class="vrc-profile-name">${esc(u.displayName)}</div><div class="vrc-profile-status"><span class="vrc-status-dot ${statusDotClass(u.status)}"></span>${getStatusText(u.status, u.statusDescription)}</div></div><span class="msi" style="font-size:16px;color:var(--tx3);flex-shrink:0;">manage_accounts</span></div>`;
}

// My Profile Modal
function openMyProfileModal() {
    if (!currentVrcUser) return;
    const m = document.getElementById('modalMyProfile');
    if (!m) return;
    renderMyProfileContent();
    m.style.display = 'flex';
}

function closeMyProfile() {
    const m = document.getElementById('modalMyProfile');
    if (m) m.style.display = 'none';
}

function renderMyProfileContent() {
    const u = currentVrcUser;
    const box = document.getElementById('mypBox');
    if (!u || !box) return;

    const changeBannerTitle = t('profiles.my_profile.change_banner', 'Change banner');
    const addBannerTitle = t('profiles.my_profile.add_banner', 'Add banner');
    const bannerLabel = t('profiles.my_profile.banner', 'Banner');
    const changeIconTitle = t('profiles.my_profile.change_icon', 'Change icon');
    const noLanguagesLabel = t('profiles.my_profile.empty.no_languages', 'No languages set');
    const noLinksLabel = t('profiles.my_profile.empty.no_links', 'No links added');
    const noPronounsLabel = t('profiles.my_profile.empty.no_pronouns', 'No pronouns set');
    const noBioLabel = t('profiles.my_profile.empty.no_bio', 'No bio written yet');
    const addLanguageLabel = t('profiles.my_profile.add_language', 'Add language...');

    const bannerSrc = u.profilePicOverride || u.currentAvatarImageUrl || u.image || '';
    const bannerHtml = bannerSrc
        ? `<div class="fd-banner"><img src="${esc(bannerSrc)}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div><button class="myp-edit-btn" style="position:absolute;top:8px;right:8px;z-index:2;" onclick="openImagePicker('profile-banner')" title="${esc(changeBannerTitle)}"><span class="msi" style="font-size:13px;">edit</span></button></div>`
        : `<div style="display:flex;justify-content:flex-end;padding:4px 0 2px 0;"><button class="myp-edit-btn" onclick="openImagePicker('profile-banner')" title="${esc(addBannerTitle)}"><span class="msi" style="font-size:13px;">edit</span><span style="font-size:11px;margin-left:3px;">${esc(bannerLabel)}</span></button></div>`;

    const avatarImg = u.image
        ? `<img class="myp-avatar" src="${esc(u.image)}" onerror="this.outerHTML='<div class=\\'myp-avatar myp-avatar-fb\\'>${esc((u.displayName||'?')[0])}</div>'">`
        : `<div class="myp-avatar myp-avatar-fb">${esc((u.displayName||'?')[0])}</div>`;
    const imgTag = `<div style="position:relative;display:inline-block;flex-shrink:0;">${avatarImg}<button class="myp-edit-btn" style="position:absolute;bottom:-4px;right:-4px;padding:2px;min-width:0;width:18px;height:18px;display:flex;align-items:center;justify-content:center;" onclick="openImagePicker('profile-icon')" title="${esc(changeIconTitle)}"><span class="msi" style="font-size:11px;">edit</span></button></div>`;

    const langTags = (u.tags||[]).filter(t => t.startsWith('language_'));
    const langsViewHtml = langTags.length
        ? `<div class="fd-lang-tags">${langTags.map(t => `<span class="vrcn-badge">${esc(LANG_MAP[t]||t.replace('language_','').toUpperCase())}</span>`).join('')}</div>`
        : `<div class="myp-empty">${noLanguagesLabel}</div>`;

    const bioLinksViewHtml = (u.bioLinks||[]).length
        ? `<div class="fd-bio-links">${(u.bioLinks).map(bl => renderBioLink(bl)).join('')}</div>`
        : `<div class="myp-empty">${noLinksLabel}</div>`;

    const _repG = (typeof myGroups !== 'undefined') && myGroups.find(g => g.isRepresenting === true);
    let repGroupHtml = '';
    if (_repG) {
        const _repIcon = _repG.iconUrl
            ? `<img class="fd-group-icon" src="${esc(_repG.iconUrl)}" onerror="this.style.display='none'">`
            : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
        repGroupHtml = `<div class="myp-section" style="padding-bottom:4px;">
            <div class="fd-group-rep-label">${t('profiles.badges.representing', 'Representing')}</div>
            <div class="fd-group-card fd-group-rep" onclick="closeMyProfile();openGroupDetail('${esc(_repG.id)}')">
                ${_repIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(_repG.name)}</div><div class="fd-group-card-meta">${esc(_repG.shortCode || '')}${_repG.discriminator ? '.' + esc(_repG.discriminator) : ''}${_repG.memberCount ? ' &middot; ' + esc(getGroupMemberText(_repG.memberCount)) : ''}</div></div>
            </div>
        </div>`;
    }

    box.innerHTML = `
        ${bannerHtml}
        <div class="fd-content${bannerSrc ? ' fd-has-banner' : ''}">
            <div class="myp-header">
                ${imgTag}
                <div class="myp-header-info">
                    <div class="myp-name">${esc(u.displayName)}</div>
                    <div class="myp-status-row" onclick="openStatusModal()">
                        <span class="vrc-status-dot ${statusDotClass(u.status)}" style="width:7px;height:7px;flex-shrink:0;"></span>
                        <span>${getStatusText(u.status, u.statusDescription)}</span>
                        <span class="msi" style="font-size:13px;opacity:.45;margin-left:2px;">edit</span>
                    </div>
                </div>
            </div>

            ${repGroupHtml}
            ${_renderMyBadgesSection(u)}

            <div class="myp-section">
                <div class="myp-section-header">
                    <span class="myp-section-title">${t('profiles.my_profile.sections.pronouns', 'Pronouns')}</span>
                    <button class="myp-edit-btn" onclick="editMyField('pronouns')"><span class="msi" style="font-size:14px;">edit</span></button>
                </div>
                <div id="mypPronounsView">
                    ${u.pronouns ? `<div style="font-size:13px;color:var(--tx1);">${esc(u.pronouns)}</div>` : `<div class="myp-empty">${noPronounsLabel}</div>`}
                </div>
                <div id="mypPronounsEdit" style="display:none;">
                    <input type="text" id="mypPronounsInput" class="vrcn-edit-field" placeholder="${esc(t('profiles.my_profile.pronouns_placeholder', 'e.g. he/him, she/her, they/them...'))}" maxlength="32" value="${esc(u.pronouns||'')}" style="width:100%;">
                    <div class="myp-edit-actions">
                        <button class="vrcn-button" onclick="cancelMyField('pronouns')">${t('common.cancel', 'Cancel')}</button>
                        <button class="vrcn-button vrcn-btn-primary" onclick="saveMyField('pronouns')">${t('common.save', 'Save')}</button>
                    </div>
                </div>
            </div>

            <div class="myp-section">
                <div class="myp-section-header">
                    <span class="myp-section-title">${t('profiles.my_profile.sections.bio', 'Bio')}</span>
                    <button class="myp-edit-btn" onclick="editMyField('bio')"><span class="msi" style="font-size:14px;">edit</span></button>
                </div>
                <div id="mypBioView">
                    ${u.bio ? `<div class="fd-bio">${esc(u.bio)}</div>` : `<div class="myp-empty">${noBioLabel}</div>`}
                </div>
                <div id="mypBioEdit" style="display:none;">
                    <textarea id="mypBioInput" class="myp-textarea" rows="4" maxlength="512" placeholder="${esc(t('profiles.my_profile.bio_placeholder', 'Write your bio...'))}">${esc(u.bio||'')}</textarea>
                    <div class="myp-char-count"><span id="mypBioCount">${(u.bio||'').length}</span>/512</div>
                    <div class="myp-edit-actions">
                        <button class="vrcn-button" onclick="cancelMyField('bio')">${t('common.cancel', 'Cancel')}</button>
                        <button class="vrcn-button vrcn-btn-primary" onclick="saveMyField('bio')">${t('common.save', 'Save')}</button>
                    </div>
                </div>
            </div>

            <div class="myp-section">
                <div class="myp-section-header">
                    <span class="myp-section-title">${t('profiles.my_profile.sections.links', 'Links')}</span>
                    <button class="myp-edit-btn" onclick="editMyField('links')"><span class="msi" style="font-size:14px;">edit</span></button>
                </div>
                <div id="mypLinksView">${bioLinksViewHtml}</div>
                <div id="mypLinksEdit" style="display:none;">
                    <div id="mypLinksInputs"></div>
                    <div class="myp-edit-actions">
                        <button class="vrcn-button" onclick="cancelMyField('links')">${t('common.cancel', 'Cancel')}</button>
                        <button class="vrcn-button vrcn-btn-primary" onclick="saveMyField('links')">${t('common.save', 'Save')}</button>
                    </div>
                </div>
            </div>

            <div class="myp-section">
                <div class="myp-section-header">
                    <span class="myp-section-title">${t('profiles.my_profile.sections.languages', 'Languages')}</span>
                    <button class="myp-edit-btn" onclick="editMyField('languages')"><span class="msi" style="font-size:14px;">edit</span></button>
                </div>
                <div id="mypLangsView">${langsViewHtml}</div>
                <div id="mypLangsEdit" style="display:none;">
                    <div id="mypLangsChips" class="myp-lang-chips"></div>
                    <div class="myp-lang-add-row">
                        <select id="mypLangSelect" class="myp-lang-select"><option value="">${addLanguageLabel}</option></select>
                        <button class="myp-add-lang-btn" onclick="addMyLanguage()"><span class="msi" style="font-size:15px;">add</span></button>
                    </div>
                    <div class="myp-edit-actions">
                        <button class="vrcn-button" onclick="cancelMyField('languages')">${t('common.cancel', 'Cancel')}</button>
                        <button class="vrcn-button vrcn-btn-primary" onclick="saveMyField('languages')">${t('common.save', 'Save')}</button>
                    </div>
                </div>
            </div>

            <div style="text-align:right;padding-top:12px;">
                <button class="vrcn-button-round" onclick="closeMyProfile()">${t('common.close', 'Close')}</button>
            </div>
        </div>`;

    const myStatusTextEl = box.querySelector('.myp-status-row span:nth-of-type(2)');
    if (myStatusTextEl) myStatusTextEl.textContent = getStatusText(u.status, u.statusDescription);

    const bioInput = document.getElementById('mypBioInput');
    if (bioInput) bioInput.oninput = () => {
        const cnt = document.getElementById('mypBioCount');
        if (cnt) cnt.textContent = bioInput.value.length;
    };
}

let _myBadgesEditing = false;

function _renderMyBadgesSection(u) {
    const badges = u.badges || [];
    if (badges.length === 0) return '';
    const noBadgesLabel = t('profiles.my_profile.empty.no_badges', 'No badges');
    const badgesTitle = t('profiles.my_profile.sections.badges', 'Badges');
    const iconsHtml = badges.map(b => {
        const hidden = !b.showcased;
        return `<div class="myp-badge-item fd-vrc-badge-wrap${hidden ? ' myp-badge-hidden' : ''}${_myBadgesEditing ? ' myp-badge-editing' : ''}" data-badge-id="${esc(b.id)}" data-badge-img="${esc(b.imageUrl)}" data-badge-name="${encodeURIComponent(b.name)}" data-badge-desc="${encodeURIComponent(b.description || '')}" onclick="${_myBadgesEditing ? `toggleMyBadge('${esc(b.id)}')` : ''}"><img class="fd-vrc-badge-icon" src="${esc(b.imageUrl)}" alt="${esc(b.name)}" onerror="this.closest('.myp-badge-item').style.display='none'"></div>`;
    }).join('');
    return `<div class="myp-section">
        <div class="myp-section-header">
            <span class="myp-section-title">${badgesTitle}</span>
            <button class="myp-edit-btn" onclick="toggleBadgeEditMode()"><span class="msi" style="font-size:14px;">${_myBadgesEditing ? 'check' : 'edit'}</span></button>
        </div>
        <div class="myp-badges-row">${iconsHtml}</div>
    </div>`;
}

function toggleBadgeEditMode() {
    _myBadgesEditing = !_myBadgesEditing;
    renderMyProfileContent();
}

function toggleMyBadge(badgeId) {
    if (!currentVrcUser?.badges) return;
    const b = currentVrcUser.badges.find(x => x.id === badgeId);
    if (!b) return;
    const newShowcased = !b.showcased;
    // Optimistic update
    b.showcased = newShowcased;
    const wrap = document.querySelector(`.myp-badge-item[data-badge-id="${badgeId}"]`);
    if (wrap) wrap.classList.toggle('myp-badge-hidden', !newShowcased);
    sendToCS({ action: 'vrcUpdateBadge', badgeId, showcased: newShowcased });
}

function editMyField(field) {
    const VIEWS = { pronouns: 'mypPronounsView', bio: 'mypBioView', links: 'mypLinksView', languages: 'mypLangsView' };
    const EDITS = { pronouns: 'mypPronounsEdit', bio: 'mypBioEdit', links: 'mypLinksEdit', languages: 'mypLangsEdit' };
    // Close other open edit panels
    Object.keys(VIEWS).forEach(f => {
        if (f !== field) {
            const v = document.getElementById(VIEWS[f]); if (v) v.style.display = '';
            const e = document.getElementById(EDITS[f]); if (e) e.style.display = 'none';
        }
    });
    const viewEl = document.getElementById(VIEWS[field]);
    const editEl = document.getElementById(EDITS[field]);
    if (viewEl) viewEl.style.display = 'none';
    if (editEl) editEl.style.display = '';

    if (field === 'pronouns') {
        const inp = document.getElementById('mypPronounsInput');
        if (inp) { inp.value = currentVrcUser.pronouns || ''; inp.focus(); }
    } else if (field === 'bio') {
        const inp = document.getElementById('mypBioInput');
        if (inp) { inp.focus(); const cnt = document.getElementById('mypBioCount'); if (cnt) cnt.textContent = inp.value.length; }
    } else if (field === 'links') {
        _renderMyLinksInputs();
    } else if (field === 'languages') {
        _renderMyLangsEdit();
    }
}

function cancelMyField(field) {
    const VIEWS = { pronouns: 'mypPronounsView', bio: 'mypBioView', links: 'mypLinksView', languages: 'mypLangsView' };
    const EDITS = { pronouns: 'mypPronounsEdit', bio: 'mypBioEdit', links: 'mypLinksEdit', languages: 'mypLangsEdit' };
    const v = document.getElementById(VIEWS[field]); if (v) v.style.display = '';
    const e = document.getElementById(EDITS[field]); if (e) e.style.display = 'none';
}

function saveMyField(field) {
    const u = currentVrcUser;
    if (!u) return;
    const EDITS = { pronouns: 'mypPronounsEdit', bio: 'mypBioEdit', links: 'mypLinksEdit', languages: 'mypLangsEdit' };
    const saveBtn = document.querySelector(`#${EDITS[field]} .vrcn-btn-primary`);
    if (saveBtn) saveBtn.disabled = true;

    if (field === 'pronouns') {
        const pronouns = document.getElementById('mypPronounsInput')?.value ?? '';
        sendToCS({ action: 'vrcUpdateProfile', pronouns });
    } else if (field === 'bio') {
        const bio = document.getElementById('mypBioInput')?.value ?? '';
        sendToCS({ action: 'vrcUpdateProfile', bio });
    } else if (field === 'links') {
        const inputs = document.querySelectorAll('#mypLinksInputs .vrcn-edit-field');
        const bioLinks = Array.from(inputs).map(i => i.value.trim()).filter(Boolean).slice(0, 3);
        sendToCS({ action: 'vrcUpdateProfile', bioLinks });
    } else if (field === 'languages') {
        const chips = document.querySelectorAll('#mypLangsChips [data-lang]');
        const selectedLangs = Array.from(chips).map(c => c.dataset.lang);
        const nonLangTags = (u.tags||[]).filter(t => !t.startsWith('language_'));
        sendToCS({ action: 'vrcUpdateProfile', tags: [...nonLangTags, ...selectedLangs] });
    }
}

function _renderMyLinksInputs() {
    const container = document.getElementById('mypLinksInputs');
    if (!container) return;
    const links = currentVrcUser.bioLinks || [];
    container.innerHTML = [0, 1, 2].map(i =>
        `<div class="myp-link-row">
            <span class="myp-link-num">${i + 1}</span>
            <input type="url" class="vrcn-edit-field" placeholder="https://..." value="${esc(links[i]||'')}" maxlength="512" style="flex:1;">
        </div>`
    ).join('');
}

function _renderMyLangsEdit() {
    const selectedLangs = (currentVrcUser.tags||[]).filter(t => t.startsWith('language_'));
    _renderMyLangChips(selectedLangs, document.getElementById('mypLangsChips'));
    const sel = document.getElementById('mypLangSelect');
    if (!sel) return;
    sel.innerHTML = `<option value="">${t('profiles.my_profile.add_language', 'Add language...')}</option>`;
    Object.entries(LANG_MAP).forEach(([key, name]) => {
        if (!selectedLangs.includes(key))
            sel.insertAdjacentHTML('beforeend', `<option value="${key}">${esc(name)}</option>`);
    });
}

function _renderMyLangChips(langs, el) {
    if (!el) return;
    el.innerHTML = langs.map(tag =>
        `<span class="myp-lang-chip" data-lang="${tag}">${esc(LANG_MAP[tag]||tag.replace('language_','').toUpperCase())}<button class="myp-lang-remove" onclick="removeMyLanguage('${tag}')"><span class="msi" style="font-size:11px;">close</span></button></span>`
    ).join('');
}

function addMyLanguage() {
    const sel = document.getElementById('mypLangSelect');
    const key = sel?.value;
    if (!key) return;
    const chips = Array.from(document.querySelectorAll('#mypLangsChips [data-lang]')).map(c => c.dataset.lang);
    if (chips.includes(key)) return;
    chips.push(key);
    _renderMyLangChips(chips, document.getElementById('mypLangsChips'));
    const opt = sel.querySelector(`option[value="${key}"]`);
    if (opt) opt.remove();
    sel.value = '';
}

function removeMyLanguage(tag) {
    const chips = Array.from(document.querySelectorAll('#mypLangsChips [data-lang]')).map(c => c.dataset.lang).filter(t => t !== tag);
    _renderMyLangChips(chips, document.getElementById('mypLangsChips'));
    const sel = document.getElementById('mypLangSelect');
    if (sel) sel.insertAdjacentHTML('beforeend', `<option value="${tag}">${esc(LANG_MAP[tag]||tag.replace('language_','').toUpperCase())}</option>`);
}


function renderVrcFriends(friends, counts) {
    const el = document.getElementById('vrcFriendsList');
    const lp = document.getElementById('vrcLoginPrompt');
    if (lp) lp.style.display = 'none';
    vrcFriendsData = friends || [];

    if (currentFriendDetail && friends) {
        const lf = friends.find(f => f.id === currentFriendDetail.id);
        if (lf) {
            currentFriendDetail.status = lf.status;
            currentFriendDetail.statusDescription = lf.statusDescription;
            currentFriendDetail.location = lf.location;
            currentFriendDetail.presence = lf.presence;
            const detailStatusEl = document.getElementById('fd-live-status');
            if (detailStatusEl) {
                const isWeb = lf.presence === 'web';
                const isOff = lf.presence === 'offline';
                const dotClass = isWeb ? 'vrc-status-ring' : 'vrc-status-dot';
                detailStatusEl.innerHTML = `<span class="${dotClass} ${isOff ? 's-offline' : statusDotClass(lf.status)}" style="width:8px;height:8px;"></span>${isOff ? t('status.offline', 'Offline') : statusLabel(lf.status)}${(!isOff && isWeb) ? ' ' + t('profiles.friends.web_suffix', '(Web)') : ''}${(!isOff && lf.statusDescription) ? ' - ' + esc(lf.statusDescription) : ''}`;
            }
        }
    }

    const searchBar = document.getElementById('vrcFriendSearch');
    if (searchBar) searchBar.style.display = vrcFriendsData.length > 0 ? '' : 'none';

    if (!friends || !friends.length) {
        el.innerHTML = `<div class="vrc-section-label">${getFriendSectionLabel('onlineZero', 0)}</div><div style="padding:16px;text-align:center;font-size:12px;color:var(--tx3);">${t('dashboard.friends.empty', 'No friends online')}</div>`;
        return;
    }

    const gameFriends = friends.filter(f => f.presence === 'game');
    const webFriends = friends.filter(f => f.presence === 'web');
    const offlineFriends = friends.filter(f => f.presence === 'offline');

    const gc = counts ? counts.game : gameFriends.length;
    const wc = counts ? counts.web : webFriends.length;
    const oc = counts ? counts.offline : offlineFriends.length;

    const renderCard = (f, presenceType) => {
        const img = f.image || '';
        const imgTag = img
            ? `<img class="vrc-friend-avatar" src="${img}" onerror="this.style.display='none'">`
            : `<div class="vrc-friend-avatar" style="display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:700;color:var(--tx3)">${esc((f.displayName || '?')[0])}</div>`;
        const dotClass = presenceType === 'web' ? 'vrc-status-ring' : 'vrc-status-dot';
        const statusCls = presenceType === 'offline' ? 's-offline' : statusDotClass(f.status);
        const rank = getTrustRank(f.tags || []);
        const rankBadge = rank ? `<span class="vrcn-badge" style="background:${rank.color}22;color:${rank.color};">${rank.label}</span>` : '';
        const fid = (f.id || '').replace(/'/g, "\\'");
        const statusText = f.statusDescription || statusLabel(f.status);
        const locationText = getFriendLocationLabel(presenceType, f.location);
        return `<div class="vrc-friend-card" data-status="${statusCls}" onclick="openFriendDetail('${fid}')">${imgTag}<div class="vrc-friend-info"><div class="vrc-friend-name" style="display:flex;align-items:center;gap:5px;"><span class="${dotClass} ${statusCls}" style="width:6px;height:6px;flex-shrink:0;"></span><span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(f.displayName)}</span>${rankBadge}</div><div class="vrc-friend-loc">${esc(statusText)} &middot; ${esc(locationText)}</div></div></div>`;
    };

    const favIds = new Set(favFriendsData.map(f => f.favoriteId));
    const favFriends = favIds.size > 0 ? [...friends].filter(f => favIds.has(f.id)).sort((a, b) => {
        const order = { game: 0, web: 1, offline: 2 };
        return (order[a.presence] ?? 2) - (order[b.presence] ?? 2);
    }) : [];

    let h = '';
    const appendSection = (key, count, list, presenceResolver) => {
        if (!list.length) return;
        const chev = friendSectionCollapsed[key] ? 'expand_more' : 'expand_less';
        h += `<div class="vrc-section-label vrc-offline-toggle" onclick="toggleFriendSection('${key}')" style="cursor:pointer;"><span class="vrc-section-text">${getFriendSectionLabel(key, count)}</span><span class="vrc-section-short">${getFriendSectionShortLabel(key)}</span><span class="msi" style="font-size:14px;" id="${key}Chevron">${chev}</span></div>`;
        h += `<div id="${key}FriendsSection" style="display:${friendSectionCollapsed[key] ? 'none' : ''};">`;
        list.forEach(f => {
            const resolvedPresence = typeof presenceResolver === 'function' ? presenceResolver(f) : presenceResolver;
            h += renderCard(f, resolvedPresence);
        });
        h += `</div>`;
    };

    // Same Location — only shown when sidebar is expanded
    if (!rsidebarCollapsed) {
        const _instGroups = {};
        friends.filter(f => f.presence === 'game' && f.location && f.location.startsWith('wrld_')).forEach(f => {
            const locBase = f.location.split('~')[0];
            if (!_instGroups[locBase]) _instGroups[locBase] = [];
            _instGroups[locBase].push(f);
        });
        const _sharedInst = Object.entries(_instGroups).filter(([, list]) => list.length >= 2);
        if (_sharedInst.length) {
            const _slTotal = _sharedInst.reduce((s, [, l]) => s + l.length, 0);
            const _slChev = friendSectionCollapsed.samelocation ? 'expand_more' : 'expand_less';
            h += `<div class="vrc-section-label vrc-offline-toggle" onclick="toggleFriendSection('samelocation')" style="cursor:pointer;"><span class="vrc-section-text">${tf('profiles.friends.sections.same_location', { count: _slTotal }, 'IN INSTANCE - {count}')}</span><span class="vrc-section-short">${t('profiles.friends.sections_short.same_location', 'HERE')}</span><span class="msi" style="font-size:14px;" id="samelocationChevron">${_slChev}</span></div>`;
            h += `<div id="samelocationFriendsSection" style="display:${friendSectionCollapsed.samelocation ? 'none' : ''};">`;
            _sharedInst.forEach(([locBase, list]) => {
                const _wid = locBase.split(':')[0];
                const _iid = locBase.split(':')[1] || '';
                const _wc = (typeof dashWorldCache !== 'undefined' && dashWorldCache[_wid]) || null;
                const _wname = _wc?.name || '';
                const _wthumb = _wc?.thumbnailImageUrl || _wc?.imageUrl || '';
                const _grpLabel = _wname
                    ? `${_wname}${_iid ? ' · #' + _iid : ''}`
                    : (_iid ? '#' + _iid : _wid);
                const { instanceType: _iType } = parseFriendLocation(list[0]?.location || '');
                const { cls: _iCls, label: _iLabel } = getInstanceBadge(_iType);
                const _badgeHtml = `<span class="vrcn-badge ${_iCls}">${esc(_iLabel)}</span>`;
                h += `<div class="sloc-inst-card">`;
                if (_wthumb) h += `<div class="sloc-inst-bg" style="background-image:url('${cssUrl(_wthumb)}')"></div>`;
                h += `<div class="sloc-inst-content">`;
                h += `<div class="sloc-inst-label">${esc(_grpLabel)} <span class="sloc-inst-count">${list.length}</span>${_badgeHtml}</div>`;
                list.forEach(f => { h += renderCard(f, 'game'); });
                h += `</div></div>`;
            });
            h += `</div>`;
        }
    }

    appendSection('favorites', favFriends.length, favFriends, f => f.presence);
    appendSection('ingame', gc, gameFriends, 'game');
    appendSection('web', wc, webFriends, 'web');
    appendSection('offline', oc, offlineFriends, 'offline');

    el.innerHTML = h;
    filterFriendsList();
}

function toggleFriendSection(key) {
    friendSectionCollapsed[key] = !friendSectionCollapsed[key];
    try { localStorage.setItem('friendSectionCollapsed', JSON.stringify(friendSectionCollapsed)); } catch {}
    const ids = { samelocation: ['samelocationFriendsSection', 'samelocationChevron'], favorites: ['favoritesFriendsSection', 'favoritesChevron'], ingame: ['ingameFriendsSection', 'ingameChevron'], web: ['webFriendsSection', 'webChevron'], offline: ['offlineFriendsSection', 'offlineChevron'] };
    const [secId, chevId] = ids[key] || [];
    const sec = secId && document.getElementById(secId);
    const chev = chevId && document.getElementById(chevId);
    if (sec) sec.style.display = friendSectionCollapsed[key] ? 'none' : '';
    if (chev) chev.textContent = friendSectionCollapsed[key] ? 'expand_more' : 'expand_less';
}

function filterFriendsList() {
    const q = (document.getElementById('vrcFriendSearchInput')?.value || '').toLowerCase().trim();
    const cards = document.querySelectorAll('#vrcFriendsList .vrc-friend-card');
    const sections = document.querySelectorAll('#vrcFriendsList .vrc-section-label');

    // Hide favorites + samelocation sections during search to avoid duplicates
    const favSec    = document.getElementById('favoritesFriendsSection');
    const favLabel  = favSec?.previousElementSibling;
    const slocSec   = document.getElementById('samelocationFriendsSection');
    const slocLabel = slocSec?.previousElementSibling;

    const sectionMap = {
        ingame:  document.getElementById('ingameFriendsSection'),
        web:     document.getElementById('webFriendsSection'),
        offline: document.getElementById('offlineFriendsSection'),
    };

    if (!q) {
        // Reset: show all cards, restore all collapsed states
        cards.forEach(c => c.style.display = '');
        sections.forEach(s => s.style.display = '');
        Object.entries(sectionMap).forEach(([key, el]) => {
            if (el) el.style.display = friendSectionCollapsed[key] ? 'none' : '';
        });
        if (favSec)    favSec.style.display    = friendSectionCollapsed.favorites    ? 'none' : '';
        if (favLabel)  favLabel.style.display   = '';
        if (slocSec)   slocSec.style.display    = friendSectionCollapsed.samelocation ? 'none' : '';
        if (slocLabel) slocLabel.style.display  = '';
        return;
    }

    // Hide favorites + samelocation while searching (prevents duplicates)
    if (favSec)    favSec.style.display    = 'none';
    if (favLabel)  favLabel.style.display  = 'none';
    if (slocSec)   slocSec.style.display   = 'none';
    if (slocLabel) slocLabel.style.display = 'none';

    // Force-expand all other sections so collapsed cards are still searchable
    Object.values(sectionMap).forEach(el => { if (el) el.style.display = ''; });

    cards.forEach(c => {
        const name = (c.querySelector('.vrc-friend-name')?.textContent || '').toLowerCase();
        c.style.display = name.includes(q) ? '' : 'none';
    });

    // Hide section labels if all their cards are hidden
    sections.forEach(s => {
        if (s.style.display === 'none') return; // already hidden (e.g. favorites during search)
        let hasVisible = false;
        let sibling = s.nextElementSibling;
        while (sibling && !sibling.classList.contains('vrc-section-label')) {
            if (sibling.classList.contains('vrc-friend-card') && sibling.style.display !== 'none') hasVisible = true;
            // Check inside any section wrapper div
            if (sibling.id && sibling.id.endsWith('FriendsSection')) {
                sibling.querySelectorAll('.vrc-friend-card').forEach(c => {
                    if (c.style.display !== 'none') hasVisible = true;
                });
            }
            sibling = sibling.nextElementSibling;
        }
        s.style.display = hasVisible ? '' : 'none';
    });
}

function openStatusModal() {
    if (!currentVrcUser) return;
    selectedStatus = currentVrcUser.status || 'active';
    const m = document.getElementById('modalStatus');
    const opts = document.getElementById('statusOptions');
    opts.innerHTML = STATUS_LIST.map(s =>
        `<div class="status-option${selectedStatus === s.key ? ' selected' : ''}" data-status-key="${s.key}" onclick="selectStatusOption('${s.key}')"><div class="status-option-dot" style="background:${s.color}"></div><div><div class="status-option-label">${t(s.labelKey || '', s.label)}</div><div class="status-option-desc">${t(s.descKey || '', s.desc)}</div></div></div>`
    ).join('');
    const inp = document.getElementById('statusDescInput');
    inp.value = currentVrcUser.statusDescription || '';
    document.getElementById('statusDescCount').textContent = (inp.value.length) + '/32';
    inp.oninput = () => {
        document.getElementById('statusDescCount').textContent = inp.value.length + '/32';
    };
    m.style.display = 'flex';
    setTimeout(() => inp.focus(), 100);
}

function selectStatusOption(key) {
    selectedStatus = key;
    document.querySelectorAll('.status-option').forEach(el => {
        el.classList.toggle('selected', el.dataset.statusKey === key);
    });
}

function submitStatusChange() {
    const desc = document.getElementById('statusDescInput').value.trim();
    sendToCS({ action: 'vrcUpdateStatus', status: selectedStatus, statusDescription: desc });
    document.getElementById('modalStatus').style.display = 'none';
}

// Profile helpers
function formatDuration(totalSec) {
    if (totalSec < 1) return '0s';
    const d = Math.floor(totalSec / 86400);
    const h = Math.floor((totalSec % 86400) / 3600);
    const m = Math.floor((totalSec % 3600) / 60);
    const s = totalSec % 60;
    if (d > 0) return `${d}d ${h}h ${m}m ${s}s`;
    if (h > 0) return `${h}h ${m}m ${s}s`;
    if (m > 0) return `${m}m ${s}s`;
    return `${s}s`;
}

function formatLastSeen(apiLastLogin, localLastSeen) {
    let best = null;
    if (apiLastLogin) {
        const d = new Date(apiLastLogin);
        if (!isNaN(d)) best = d;
    }
    if (localLastSeen) {
        const d = new Date(localLastSeen);
        if (!isNaN(d) && (!best || d > best)) best = d;
    }
    if (!best) return '';
    const now = new Date();
    const diff = now - best;
    if (diff < 60000) return t('profiles.last_seen.just_now', 'Just now');
    if (diff < 3600000) return tf('profiles.last_seen.minutes_ago', { count: Math.floor(diff / 60000) }, '{count}m ago');
    if (diff < 86400000) return tf('profiles.last_seen.hours_ago', { count: Math.floor(diff / 3600000) }, '{count}h ago');
    if (diff < 604800000) return tf('profiles.last_seen.days_ago', { count: Math.floor(diff / 86400000) }, '{count}d ago');
    return best.toLocaleDateString(t('clock.date_locale', getLanguageLocale()));
}

function fdEditNote() {
    document.getElementById('fdVrcNoteView')?.style.setProperty('display', 'none');
    const edit = document.getElementById('fdVrcNoteEdit');
    if (edit) edit.style.display = '';
    const inp = document.getElementById('fdVrcNoteInput');
    if (inp) { inp.value = currentFriendDetail?.note || ''; inp.focus(); }
}

function fdCancelNote() {
    const view = document.getElementById('fdVrcNoteView');
    if (view) view.style.display = '';
    const edit = document.getElementById('fdVrcNoteEdit');
    if (edit) edit.style.display = 'none';
    const btn = document.getElementById('fdVrcNoteSaveBtn');
    if (btn) btn.disabled = false;
}

function fdSaveNote() {
    const inp = document.getElementById('fdVrcNoteInput');
    if (!inp || !currentFriendDetail) return;
    const btn = document.getElementById('fdVrcNoteSaveBtn');
    if (btn) btn.disabled = true;
    sendToCS({ action: 'vrcUpdateNote', userId: currentFriendDetail.id, note: inp.value });
}

function openFriendDetail(userId) {
    const m = document.getElementById('modalFriendDetail');
    const c = document.getElementById('friendDetailContent');
    c.innerHTML = sk('profile');
    m.style.display = 'flex';
    sendToCS({ action: 'vrcGetFriendDetail', userId: userId });
}

function closeFriendDetail() {
    if (_fdLiveTimer) { clearInterval(_fdLiveTimer); _fdLiveTimer = null; }
    document.getElementById('modalFriendDetail').style.display = 'none';
    currentFriendDetail = null;
    window._fdAllMutuals = null;
}


function lookupAndOpenAvatar(fileId, iconEl) {
    if (iconEl) iconEl.style.opacity = '0.4';
    sendToCS({ action: 'vrcLookupAvatarByFileId', fileId, openModal: true });
}

function handleAvatarByFileId(payload) {
    if (payload.avatarId) {
        const section = document.getElementById('fdAvatarSection');
        if (section) {
            const avImg = currentFriendDetail?.currentAvatarImageUrl || '';
            const avIcon = avImg
                ? `<img class="fd-group-icon" src="${esc(avImg)}" onerror="this.style.display='none'">`
                : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">checkroom</span></div>`;
            const authorHtml = payload.avatarAuthor
                ? `<div class="fd-group-card-meta">${esc(payload.avatarAuthor)}</div>` : '';
            section.innerHTML = `<div class="fd-group-rep-label">${t('profiles.badges.current_avatar', 'Current Avatar')}</div>
                <div class="fd-group-card fd-group-rep" onclick="openAvatarDetail('${payload.avatarId}')">
                    ${avIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(payload.avatarName || payload.avatarId)}</div>${authorHtml}</div>
                </div>`;
        }
        if (payload.openModal) openAvatarDetail(payload.avatarId);
    }
    // on failure: do nothing — placeholder stays empty, nothing visible
}

function filterFdMutuals() {
    const q = document.getElementById('fdMutualsSearch')?.value.trim().toLowerCase() || '';
    const grid = document.getElementById('fdMutualsGrid');
    if (!grid) return;
    const filtered = q
        ? (window._fdAllMutuals || []).filter(m => (m.displayName || '').toLowerCase().includes(q))
        : (window._fdAllMutuals || []);
    grid.innerHTML = filtered.length
        ? filtered.map(mu => renderProfileItem(mu, `closeFriendDetail();openFriendDetail('${jsq(mu.id)}')`)).join('')
        : `<div style="padding:12px;grid-column:1/-1;text-align:center;font-size:12px;color:var(--tx3);">${t('profiles.mutuals.no_results', 'No results')}</div>`;
}

function switchFdTab(tab, btn) {
    document.getElementById('fdTabInfo').style.display = tab === 'info' ? '' : 'none';
    document.getElementById('fdTabGroups').style.display = tab === 'groups' ? '' : 'none';
    const mutualsEl = document.getElementById('fdTabMutuals');
    if (mutualsEl) mutualsEl.style.display = tab === 'mutuals' ? '' : 'none';
    const contentEl = document.getElementById('fdTabContent');
    if (contentEl) contentEl.style.display = tab === 'content' ? '' : 'none';
    const favsEl = document.getElementById('fdTabFavs');
    if (favsEl) favsEl.style.display = tab === 'favs' ? '' : 'none';
    document.querySelectorAll('.fd-tab').forEach(t => t.classList.remove('active'));
    if (btn) btn.classList.add('active');
    if (tab === 'favs') {
        const uid = favsEl?.dataset.userId;
        if (uid && !favsEl.dataset.loaded) {
            favsEl.dataset.loaded = '1';
            // Only show loading spinner if no content yet (cache will arrive quickly if present)
            if (!favsEl.querySelector('.fd-content-pills'))
                favsEl.innerHTML = `<div class="empty-msg">${t('profiles.favs.loading', 'Loading favorites...')}</div>`;
            sendToCS({ action: 'vrcGetUserFavWorlds', userId: uid });
        }
    }
}

function renderUserFavWorlds(payload) {
    const el = document.getElementById('fdTabFavs');
    if (!el || el.dataset.userId !== payload.userId) return;
    const groups = payload.groups || [];
    if (!groups.length) {
        el.innerHTML = `<div class="empty-msg">${t('profiles.favs.none', 'No public favorite worlds.')}</div>`;
        return;
    }

    // Preserve active pill index across background refreshes
    let activePill = 0;
    const existingPill = el.querySelector('.fd-content-pill.active');
    if (existingPill) {
        const idx = [...el.querySelectorAll('.fd-content-pill')].indexOf(existingPill);
        if (idx >= 0) activePill = idx;
    }

    // Build pills row
    let pillsHtml = `<div class="fd-content-pills">`;
    groups.forEach((g, i) => {
        const label = esc(g.displayName || g.name);
        const count = g.worlds ? g.worlds.length : 0;
        pillsHtml += `<button class="fd-tab fd-content-pill${i === activePill ? ' active' : ''}" onclick="switchFavPill(${i},this)">${label} (${count})</button>`;
    });
    pillsHtml += `</div>`;

    // Build grid panels for each group
    let panelsHtml = '';
    groups.forEach((g, i) => {
        panelsHtml += `<div id="fdFavPanel_${i}" style="${i !== activePill ? 'display:none;' : ''}">`;
        if (g.visibility === 'private') {
            panelsHtml += `<div class="empty-msg">${t('profiles.favs.private', 'This list is private.')}</div>`;
        } else if (!g.worlds || !g.worlds.length) {
            panelsHtml += `<div class="empty-msg">${t('profiles.favs.empty_group', 'Empty.')}</div>`;
        } else {
            panelsHtml += `<div class="vrcn-world-grid-small">`;
            for (const w of g.worlds) {
                const thumb = w.thumbnailImageUrl || '';
                panelsHtml += `<div class="vrcn-world-card-small" onclick="closeFriendDetail();openWorldSearchDetail('${jsq(w.id)}')">
                    <div class="vwcs-bg"${thumb ? ` style="background-image:url('${cssUrl(thumb)}')"` : ''}></div>
                    <div class="vwcs-scrim"></div>
                    <div class="vwcs-info">
                        <div class="vwcs-name">${esc(w.name)}</div>
                        <div class="vwcs-meta"><span class="msi" style="font-size:11px;">person</span>${w.occupants} <span class="msi" style="font-size:11px;">star</span>${w.favorites}</div>
                    </div>
                </div>`;
            }
            panelsHtml += `</div>`;
        }
        panelsHtml += `</div>`;
    });

    el.innerHTML = pillsHtml + panelsHtml;
}

function switchFavPill(idx, btn) {
    const el = document.getElementById('fdTabFavs');
    if (!el) return;
    el.querySelectorAll('[id^="fdFavPanel_"]').forEach((p, i) => p.style.display = i === idx ? '' : 'none');
    el.querySelectorAll('.fd-content-pill').forEach(p => p.classList.remove('active'));
    if (btn) btn.classList.add('active');
}

function switchFdContentPill(pill, btn) {
    const worldsEl = document.getElementById('fdContentWorlds');
    const avatarsEl = document.getElementById('fdContentAvatars');
    if (worldsEl) worldsEl.style.display = pill === 'worlds' ? '' : 'none';
    if (avatarsEl) avatarsEl.style.display = pill === 'avatars' ? '' : 'none';
    document.querySelectorAll('.fd-content-pill').forEach(p => p.classList.remove('active'));
    if (btn) btn.classList.add('active');

    // Avatars are pre-fetched when profile opens, no lazy-load needed
}

function renderFdUserAvatars(payload) {
    const el = document.getElementById('fdContentAvatars');
    if (!el) return;
    const avatars = payload.avatars || [];

    // Update Avatars pill count
    const avatarsPill = document.getElementById('fdAvatarsPill');
    if (avatarsPill) avatarsPill.textContent = tf('profiles.content.avatars_pill', { count: avatars.length }, 'Avatars ({count})');

    // Update Content tab count (worlds + avatars)
    const worldsCount = Array.isArray(currentFriendDetail?.userWorlds) ? currentFriendDetail.userWorlds.length : 0;
    const contentTab = document.getElementById('fdTabContentBtn');
    if (contentTab) contentTab.textContent = tf('profiles.tabs.content', { count: worldsCount + avatars.length }, 'Content ({count})');

    if (!avatars.length) {
        el.innerHTML = `<div class="empty-msg">${t('profiles.content.no_public_avatars', 'No public avatars found.')}</div>`;
        return;
    }
    // Reuse the same renderAvatarCard() from avatars.js
    el.innerHTML = '<div class="avatar-grid">' + avatars.map(a => renderAvatarCard(a, 'search')).join('') + '</div>';
    // Check if avatars still exist
    _checkAvatarsExist(avatars.map(a => a.id).filter(Boolean));
}

// Trust rank from tags (offset by 1 in API naming)
function getTrustRank(tags) {
    if (!tags || !Array.isArray(tags)) return null;
    // Order matters: check highest first
    if (tags.includes('system_trust_legend')) return { label: t('profiles.trust.trusted', 'Trusted User'), color: '#8143E6' };
    if (tags.includes('system_trust_veteran')) return { label: t('profiles.trust.trusted', 'Trusted User'), color: '#8143E6' };
    if (tags.includes('system_trust_trusted')) return { label: t('profiles.trust.known', 'Known User'), color: '#FF7B42' };
    if (tags.includes('system_trust_known'))   return { label: t('profiles.trust.user', 'User'), color: '#2BCF5C' };
    if (tags.includes('system_trust_basic'))   return { label: t('profiles.trust.new', 'New User'), color: '#1778FF' };
    return { label: t('profiles.trust.visitor', 'Visitor'), color: '#CCCCCC' };
}

function getLanguages(tags) {
    if (!tags) return [];
    return tags.filter(t => t.startsWith('language_')).map(t => LANG_MAP[t] || t.replace('language_','').toUpperCase());
}

function getPlatformInfo(hostname) {
    const h = hostname.replace('www.', '');
    if (h.includes('twitter.com') || h.includes('x.com'))          return { key: 'twitter',   label: 'Twitter/X' };
    if (h.includes('instagram.com'))                                 return { key: 'instagram', label: 'Instagram' };
    if (h.includes('tiktok.com'))                                    return { key: 'tiktok',    label: 'TikTok' };
    if (h.includes('youtube.com') || h.includes('youtu.be'))        return { key: 'youtube',   label: 'YouTube' };
    if (h.includes('discord.gg') || h.includes('discord.com'))      return { key: 'discord',   label: 'Discord' };
    if (h.includes('github.com'))                                    return { key: 'github',    label: 'GitHub' };
    if (h.includes('facebook.com') || h.includes('fb.com'))         return { key: 'facebook',  label: 'Facebook' };
    if (h.includes('twitch.tv'))                                     return { key: 'twitch',    label: 'Twitch' };
    if (h.includes('bsky.app'))                                      return { key: 'bluesky',   label: 'Bluesky' };
    if (h.includes('pixiv.net'))                                     return { key: 'pixiv',     label: 'Pixiv' };
    if (h.includes('ko-fi.com'))                                     return { key: 'kofi',      label: 'Ko-fi' };
    if (h.includes('patreon.com'))                                   return { key: 'patreon',   label: 'Patreon' };
    if (h.includes('booth.pm'))                                      return { key: 'booth',     label: 'Booth' };
    if (h.includes('vrchat.com') || h.includes('vrc.group'))        return { key: 'vrchat',    label: 'VRChat' };
    return { key: null, label: h };
}

// Bio link to SVG brand icon and label
function renderBioLink(url) {
    let platformSvg = '';
    let label = t('profiles.common.link', 'Link');
    try {
        const h = new URL(url).hostname;
        const info = getPlatformInfo(h);
        label = info.label;
        if (info.key && PLATFORM_ICONS[info.key]) {
            platformSvg = `<svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor" style="flex-shrink:0"><path d="${PLATFORM_ICONS[info.key].svg}"/></svg>`;
        } else {
            platformSvg = `<span class="msi" style="font-size:14px;">link</span>`;
        }
    } catch {
        label = t('profiles.common.link', 'Link');
        platformSvg = `<span class="msi" style="font-size:14px;">link</span>`;
    }
    const safeUrl = esc(url);
    const safeUrlJs = jsq(url);
    return `<button class="fd-bio-link" onclick="sendToCS({action:'openUrl',url:'${safeUrlJs}'})" title="${safeUrl}">${platformSvg}<span>${esc(label)}</span></button>`;
}


function fdToggleBio(btn) {
    const bio = btn.closest('.fd-group-rep-label').nextElementSibling;
    const expanded = bio.classList.toggle('expanded');
    btn.querySelector('.msi').textContent = expanded ? 'expand_less' : 'chevron_right';
}

function renderFriendDetail(d) {
    currentFriendDetail = d;
    const c = document.getElementById('friendDetailContent');
    const img = d.image || '';
    const imgTag = img
        ? `<img class="fd-avatar" src="${img}" onerror="this.style.display='none'">`
        : `<div class="fd-avatar" style="display:flex;align-items:center;justify-content:center;font-size:20px;font-weight:700;color:var(--tx3)">${esc((d.displayName || '?')[0])}</div>`;

    let worldHtml = '';
    if (d.worldName) {
        const { worldId: fdWorldId } = parseFriendLocation(d.location);
        const onclick = fdWorldId ? `closeFriendDetail();openWorldSearchDetail('${esc(fdWorldId)}')` : '';
        worldHtml = `<div style="margin-bottom:14px;"><div class="fd-group-rep-label">${t('profiles.meta.current_world', 'Current World')}</div>` + renderInstanceItem({
            thumb:        d.worldThumb || '',
            worldName:    d.worldName,
            instanceType: d.instanceType,
            userCount:    d.userCount || 0,
            capacity:     d.worldCapacity || 0,
            onclick,
        }) + `</div>`;
    } else if (d.location === 'private') {
        worldHtml = `<div style="padding:12px;background:var(--bg-input);border-radius:10px;margin-bottom:14px;font-size:12px;color:var(--tx3);text-align:center;">${t('profiles.meta.private_instance', 'Private Instance')}</div>`;
    } else if (d.location === 'traveling') {
        worldHtml = `<div style="padding:12px;background:var(--bg-input);border-radius:10px;margin-bottom:14px;font-size:12px;color:var(--tx3);text-align:center;">${t('profiles.meta.traveling', 'Traveling...')}</div>`;
    } else if (d.location === 'offline') {
        worldHtml = `<div style="padding:12px;background:var(--bg-input);border-radius:10px;margin-bottom:14px;font-size:12px;color:var(--tx3);text-align:center;">${t('status.offline', 'Offline')}</div>`;
    }

    const bioHtml = d.bio ? `
        <div class="fd-group-rep-label">${t('profiles.bio.title', 'Biography')}<button class="fd-bio-expand" onclick="fdToggleBio(this)" style="display:none"><span class="msi">chevron_right</span></button></div>
        <div class="fd-bio">${esc(d.bio)}</div>` : '';

    // Bio links (profile links from VRChat API)
    let bioLinksHtml = '';
    if (d.bioLinks && d.bioLinks.length) {
        bioLinksHtml = `<div class="fd-bio-links">${d.bioLinks.map(u => renderBioLink(u)).join('')}</div>`;
    }

    // Avatar card — empty placeholder, filled only when lookup returns real data (no flicker on failure)
    const avatarId = d.currentAvatarId || '';
    const avatarFileId = d.avatarFileId || '';
    const avatarRowHtml = (avatarId.startsWith('avtr_') || avatarFileId)
        ? `<div id="fdAvatarSection" style="margin-bottom:14px;"></div>`
        : '';

    const lastSeenStr = formatLastSeen(d.lastLogin, d.lastSeenTracked);
    const isSelf    = currentVrcUser && d.id === currentVrcUser.id;
    const fdMeetCnt = d.meets || 0;

    const _mc = (label, valueHtml) =>
        `<div><div class="myp-section-title" style="margin-bottom:3px;">${label}</div><div style="font-size:12px;color:var(--tx2);">${valueHtml}</div></div>`;

    const _metaCells = [
        _mc(t('profiles.meta.platform', 'Platform'), esc(d.lastPlatform || '—')),
        _mc(t('profiles.meta.joined',   'Joined'),   esc(d.dateJoined   || '—')),
        _mc(t('profiles.meta.last_seen','Last Seen'), esc(lastSeenStr    || '—')),
    ];
    if (!isSelf) {
        _metaCells.push(_mc(t('profiles.meta.meets', 'Meets'),
            fdMeetCnt > 0 ? String(fdMeetCnt) : `<span style="color:var(--tx3);">—</span>`));
        _metaCells.push(_mc(t('profiles.meta.time_together', 'Time Together'),
            (d.totalTimeSeconds > 0 || d.inSameInstance)
                ? `<span id="fdTimeTogether">${formatDuration(d.totalTimeSeconds)}</span>`
                : `<span style="color:var(--tx3);">${t('profiles.meta.not_tracked', 'Not tracked yet')}</span>`));
    }

    const metaHtml = `<div class="myp-section" style="padding-bottom:14px;">
        <div class="myp-section-header"><span class="myp-section-title">${t('profiles.meta.infos_title', 'Infos')}</span></div>
        <div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px 6px;">
            ${_metaCells.join('')}
        </div>
    </div>`;

    const vrcNoteHtml = `<div class="myp-section" style="padding-bottom:14px;">
        <div class="myp-section-header">
            <span class="myp-section-title">${t('profiles.notes.vrc_note', 'VRC Note')}</span>
            <button class="myp-edit-btn" onclick="fdEditNote()"><span class="msi" style="font-size:14px;">edit</span></button>
        </div>
        <div id="fdVrcNoteView">
            ${d.note ? `<div style="font-size:12px;color:var(--tx2);line-height:1.5;">${esc(d.note)}</div>`
                     : `<div class="myp-empty">${t('profiles.notes.no_note', 'No notes added yet')}</div>`}
        </div>
        <div id="fdVrcNoteEdit" style="display:none;">
            <textarea id="fdVrcNoteInput" class="myp-textarea" rows="3" placeholder="${esc(t('profiles.notes.placeholder', 'Write a note about this user...'))}"></textarea>
            <div class="myp-edit-actions">
                <button class="vrcn-button" onclick="fdCancelNote()">${t('common.cancel', 'Cancel')}</button>
                <button id="fdVrcNoteSaveBtn" class="vrcn-button vrcn-btn-primary" onclick="fdSaveNote()">${t('common.save', 'Save')}</button>
            </div>
        </div>
    </div>`;

    // Actions
    let actionsHtml = '<div class="fd-actions">';
    const loc = (d.location || '').replace(/'/g, "\\'");
    const uid = (d.id || '').replace(/'/g, "\\'");
    const isBlocked = Array.isArray(blockedData) && blockedData.some(e => e.targetUserId === d.id);
    const isMuted   = Array.isArray(mutedData)   && mutedData.some(e => e.targetUserId === d.id);
    if (d.isFriend) {
        if (d.canJoin) actionsHtml += `<button class="vrcn-button-round vrcn-btn-join" onclick="friendAction('join','${loc}','${uid}')">${t('common.join', 'Join')}</button>`;
        if (d.canRequestInvite) actionsHtml += `<button class="vrcn-button-round" onclick="friendAction('requestInvite','${loc}','${uid}')">${t('profiles.actions.request_invite', 'Request Invite')}</button>`;
        const myInInstance = currentInstanceData && currentInstanceData.location && !currentInstanceData.empty && !currentInstanceData.error;
        if (myInInstance) actionsHtml += `<button class="vrcn-button-round" onclick="openFriendInviteModal('${uid}','${esc(d.displayName).replace(/'/g, "\\'")}')">${t('instance.actions.invite', 'Invite')}</button>`;
        const favFid = (d.favFriendId || '').replace(/'/g, "\\'");
        actionsHtml += `<button class="vrcn-button-round${d.isFavorited ? ' active' : ''}" id="fdFavBtn" onclick="toggleFavFriend('${uid}','${favFid}',this)" title="${d.isFavorited ? t('profiles.actions.unfavorite', 'Unfavorite') : t('profiles.actions.favorite', 'Favorite')}" style="margin-left:auto;"><span class="msi" style="font-size:16px;">${d.isFavorited ? 'star' : 'star_outline'}</span></button>`;
    } else {
        actionsHtml += `<button class="vrcn-button-round vrcn-btn-primary" id="fdAddFriend" onclick="sendToCS({action:'vrcSendFriendRequest',userId:'${uid}'});this.disabled=true;this.textContent='${esc(t('profiles.actions.request_sent', 'Request Sent'))}';">${t('profiles.actions.add_friend', 'Add Friend')}</button>`;
    }
    actionsHtml += `<button class="vrcn-button-round vrcn-btn-danger${isMuted ? ' active' : ''}" id="fdMuteBtn" onclick="toggleMod('${uid}','mute',this)" title="${isMuted ? t('profiles.actions.unmute', 'Unmute') : t('profiles.actions.mute', 'Mute')}"><span class="msi" style="font-size:16px;">mic${isMuted ? '_off' : ''}</span></button>`;
    actionsHtml += `<button class="vrcn-button-round vrcn-btn-danger${isBlocked ? ' active' : ''}" id="fdBlockBtn" onclick="toggleMod('${uid}','block',this)" title="${isBlocked ? t('profiles.actions.unblock', 'Unblock') : t('profiles.actions.block', 'Block')}"><span class="msi" style="font-size:16px;">${isBlocked ? 'block' : 'shield'}</span></button>`;
    if (d.isFriend) actionsHtml += `<button class="vrcn-button-round vrcn-btn-danger" id="fdUnfriend" onclick="confirmUnfriend('${uid}','${esc(d.displayName).replace(/'/g, "\\'")}') " title="${t('profiles.actions.unfriend', 'Unfriend')}"><span class="msi" style="font-size:16px;">person_remove</span></button>`;
    actionsHtml += '</div>';

    // Badges
    let badgesHtml = '<div class="fd-badges-row">';
    const platBadge = getPlatformBadgeHtml(d.platform || d.lastPlatform || '');
    if (platBadge) badgesHtml += platBadge;
    if (d.isFriend) badgesHtml += `<span class="vrcn-badge ok"><span class="msi" style="font-size:11px;">check_circle</span>${t('profiles.badges.friend', 'Friend')}</span>`;
    if (d.ageVerified) badgesHtml += `<span class="vrcn-badge ok"><span class="msi" style="font-size:11px;">verified</span>18+</span>`;
    const rank = getTrustRank(d.tags || []);
    if (rank) badgesHtml += `<span class="vrcn-badge" style="background:${rank.color}22;color:${rank.color}">${esc(rank.label)}</span>`;
    const mutualCount = (d.mutuals || []).length;
    if (mutualCount > 0) badgesHtml += `<span class="vrcn-badge"><span class="msi" style="font-size:11px;">group</span>${getProfileMutualBadgeLabel(mutualCount)}</span>`;
    if (d.id) badgesHtml += idBadge(d.id);
    badgesHtml += '</div>';

    const pronounsHtml = d.pronouns ? `<div class="fd-pronouns">${esc(d.pronouns)}</div>` : '';
    const langs = getLanguages(d.tags || []);
    const langsHtml = langs.length ? `<div class="fd-lang-tags">${langs.map(l => `<span class="vrcn-badge">${esc(l)}</span>`).join('')}</div>` : '';

    // Groups data
    const allGroups = d.userGroups || [];
    let repG = d.representedGroup;
    // Fallback: find representing group from userGroups list
    if (!repG && allGroups.length > 0) {
        const repFromList = allGroups.find(g => g.isRepresenting);
        if (repFromList) repG = repFromList;
    }

    // VRChat badges (API badges like VRC+ Supporter, etc.)
    let vrcBadgesHtml = '';
    const vrcBadges = d.badges || [];
    if (vrcBadges.length > 0) {
        const iconsHtml = vrcBadges.map(b =>
            `<div class="fd-vrc-badge-wrap"` +
                ` data-badge-img="${esc(b.imageUrl)}"` +
                ` data-badge-name="${encodeURIComponent(b.name)}"` +
                ` data-badge-desc="${encodeURIComponent(b.description || '')}">` +
                `<img class="fd-vrc-badge-icon" src="${esc(b.imageUrl)}" alt="${esc(b.name)}" onerror="this.closest('.fd-vrc-badge-wrap').style.display='none'">` +
            `</div>`
        ).join('');
        vrcBadgesHtml = `<div class="fd-vrc-badges"><div class="fd-group-rep-label">${t('profiles.badges.badges', 'Badges')}</div><div class="fd-vrc-badges-row">${iconsHtml}</div></div>`;
    }

    // Represented group card for Info tab (above bio)
    let repGroupInfoHtml = '';
    if (repG && repG.id) {
        const repIcon = repG.iconUrl ? `<img class="fd-group-icon" src="${repG.iconUrl}" onerror="this.style.display='none'">` : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
        repGroupInfoHtml = `<div class="fd-group-rep-label">${t('profiles.badges.representing', 'Representing')}</div><div class="fd-group-card fd-group-rep" onclick="closeFriendDetail();openGroupDetail('${esc(repG.id)}')">
            ${repIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(repG.name)}</div><div class="fd-group-card-meta">${esc(repG.shortCode || '')}${repG.discriminator ? '.' + esc(repG.discriminator) : ''} &middot; ${esc(getGroupMemberText(repG.memberCount))}</div></div>
        </div>`;
    }

    // Groups tab content
    let groupsContent = '';

    if (repG && repG.id) {
        const repIcon = repG.iconUrl ? `<img class="fd-group-icon" src="${repG.iconUrl}" onerror="this.style.display='none'">` : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
        groupsContent += `<div class="fd-group-rep-label">${t('profiles.badges.representing', 'Representing')}</div>
        <div class="fd-group-card fd-group-rep" onclick="closeFriendDetail();openGroupDetail('${esc(repG.id)}')">
            ${repIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(repG.name)}</div><div class="fd-group-card-meta">${esc(repG.shortCode || '')}${repG.discriminator ? '.' + esc(repG.discriminator) : ''}${repG.memberCount ? ' &middot; ' + esc(getGroupMemberText(repG.memberCount, false)) : ''}</div></div>
        </div>`;
    }

    if (allGroups.length > 0) {
        const otherGroups = repG ? allGroups.filter(g => g.id !== repG.id) : allGroups;
        if (otherGroups.length > 0) {
            groupsContent += `<div class="fd-group-rep-label" style="margin-top:${repG && repG.id ? '14' : '0'}px;">${t('profiles.badges.groups', 'Groups')}</div>`;
            groupsContent += `<div style="display:grid;grid-template-columns:1fr 1fr;column-gap:6px;">`;
            otherGroups.forEach(g => {
                const gIcon = g.iconUrl ? `<img class="fd-group-icon" src="${g.iconUrl}" onerror="this.style.display='none'">` : `<div class="fd-group-icon fd-group-icon-empty"><span class="msi" style="font-size:18px;">group</span></div>`;
                groupsContent += `<div class="fd-group-card" onclick="closeFriendDetail();openGroupDetail('${esc(g.id)}')">
                    ${gIcon}<div class="fd-group-card-info"><div class="fd-group-card-name">${esc(g.name)}</div><div class="fd-group-card-meta">${g.memberCount ? esc(getGroupMemberText(g.memberCount, false)) : ''}</div></div>
                </div>`;
            });
            groupsContent += `</div>`;
        }
    }

    if (!groupsContent) groupsContent = `<div style="padding:20px;text-align:center;font-size:12px;color:var(--tx3);">${t('profiles.badges.no_groups', 'No groups')}</div>`;

    // Mutuals tab content
    const allMutuals = d.mutuals || [];
    let mutualsContent = '';
    if (d.mutualsOptedOut) {
        mutualsContent = `<div style="padding:24px 16px;text-align:center;font-size:12px;color:var(--tx3);">
            <span class="msi" style="font-size:28px;display:block;margin-bottom:8px;opacity:.5;">visibility_off</span>
            ${t('profiles.mutuals.opted_out', 'This user has disabled Shared Connections.')}
        </div>`;
    } else if (allMutuals.length === 0) {
        mutualsContent = `<div style="padding:24px 16px;text-align:center;font-size:12px;color:var(--tx3);">
            <span class="msi" style="font-size:28px;display:block;margin-bottom:8px;opacity:.5;">group_off</span>
            ${t('profiles.mutuals.empty', 'No mutual friends found.')}<br>
            <span style="font-size:10px;margin-top:6px;display:block;line-height:1.5;">
                ${t('profiles.mutuals.empty_hint', 'Requires VRChat\'s "Shared Connections" feature to be active on both accounts.')}
            </span>
        </div>`;
    } else {
        window._fdAllMutuals = allMutuals;
        mutualsContent = `<div class="search-bar-row" style="margin-bottom:6px;">
            <span class="msi search-ico">search</span>
            <input id="fdMutualsSearch" type="text" class="vrcn-input" placeholder="${esc(t('profiles.mutuals.search_placeholder', 'Search users by name...'))}" style="background:var(--bg-input);" oninput="filterFdMutuals()">
        </div>`;
        mutualsContent += '<div id="fdMutualsGrid" style="display:grid;grid-template-columns:1fr 1fr;column-gap:6px;">';
        allMutuals.forEach(mu => {
            mutualsContent += renderProfileItem(mu, `closeFriendDetail();openFriendDetail('${jsq(mu.id)}')`);
        });
        mutualsContent += '</div>';
    }

    // Mini-timeline — filled async via timelineForUser response
    const miniTlHtml = `<div class="myp-section">
        <div class="myp-section-header">
            <span class="myp-section-title">${t('nav.timeline', 'Timeline')}</span>
        </div>
        <div id="fdMiniTl" style="max-height:160px;overflow-y:auto;"></div>
    </div>`;

    // Info tab content
    const infoContent = `${worldHtml}${vrcBadgesHtml}${repGroupInfoHtml}${avatarRowHtml}${bioHtml}${bioLinksHtml}${langsHtml}${metaHtml ? '<div style="margin-bottom:14px;">' + metaHtml + '</div>' : ''}${vrcNoteHtml}${miniTlHtml}`;

    // Banner
    const bannerSrc = d.profilePicOverride || d.currentAvatarImageUrl || d.image || '';
    const bannerHtml = bannerSrc
        ? `<div class="fd-banner"><img src="${bannerSrc}" onerror="this.parentElement.style.display='none'"><div class="fd-banner-fade"></div><button class="btn-notif" style="position:absolute;top:8px;right:8px;z-index:3;" title="${esc(t('common.share','Share'))}" onclick="navigator.clipboard.writeText('https://vrchat.com/home/user/${esc(d.id)}').then(()=>showToast(true,t('common.link_copied','Link copied!')))"><span class="msi" style="font-size:20px;">share</span></button></div>`
        : '';

    // Presence
    const fdLocation = d.location || '';
    // VRChat returns state="offline" for all non-friends due to privacy, so it cannot detect offline reliably.
    // Use status as the authoritative online/offline indicator (accurate for both friends and non-friends).
    const fdIsOffline = (d.status || 'offline') === 'offline';
    const fdIsInGame = !fdIsOffline && !!fdLocation && fdLocation !== 'offline';
    const fdIsWeb = !fdIsOffline && !fdIsInGame && d.state === 'active';
    const fdDotClass = fdIsWeb ? 'vrc-status-ring' : 'vrc-status-dot';
    const fdStatusDotCls = fdIsOffline ? 's-offline' : statusDotClass(d.status);

    const hasGroups = allGroups.length > 0 || repG;
    const hasMutuals = d.mutuals !== undefined;
    const allUserWorlds = d.userWorlds || [];
    const hasWorlds = allUserWorlds.length > 0;
    const hasContent = true; // Always show Content tab (avatars can be loaded for any user)
    const hasTabs = hasGroups || hasMutuals || hasContent;

    let tabsHtml = '';
    if (hasTabs) {
        tabsHtml = `<div class="fd-tabs"><button class="fd-tab active" onclick="switchFdTab('info',this)">${t('profiles.tabs.info', 'Info')}</button>`;
        if (hasGroups) tabsHtml += `<button class="fd-tab" onclick="switchFdTab('groups',this)">${tf('profiles.tabs.groups', { count: allGroups.length }, 'Groups ({count})')}</button>`;
        if (hasMutuals) tabsHtml += `<button class="fd-tab" onclick="switchFdTab('mutuals',this)">${tf('profiles.tabs.mutuals', { count: allMutuals.length }, 'Mutuals ({count})')}</button>`;
        tabsHtml += `<button class="fd-tab" id="fdTabContentBtn" onclick="switchFdTab('content',this)">${tf('profiles.tabs.content', { count: allUserWorlds.length }, 'Content ({count})')}</button>`;
        tabsHtml += `<button class="fd-tab" onclick="switchFdTab('favs',this)">${t('profiles.tabs.favs', 'Favs.')}</button>`;
        tabsHtml += `</div>`;
    }

    // Content tab: sub-pills for Worlds and Avatars
    let worldsGridHtml = '';
    if (allUserWorlds.length) {
        worldsGridHtml = `<div class="search-grid">`;
        allUserWorlds.forEach(w => {
            const thumb = w.thumbnailImageUrl || w.imageUrl || '';
            const wid = jsq(w.id);
            const tags = (w.tags || []).filter(t => t.startsWith('author_tag_')).map(t => t.replace('author_tag_','')).slice(0,3);
            const tagsHtml = tags.length ? `<div class="cc-tags">${tags.map(t => `<span class="vrcn-badge">${esc(t)}</span>`).join('')}</div>` : '';
            worldsGridHtml += `<div class="vrcn-content-card" onclick="closeFriendDetail();openWorldSearchDetail('${wid}')">
                <div class="cc-bg" style="background-image:url('${cssUrl(thumb)}')"></div>
                <div class="cc-scrim"></div>
                <div class="cc-content">
                    <div class="cc-name">${esc(w.name)}</div>
                    <div class="cc-bottom-row">
                        <div class="cc-meta">${esc(w.authorName || d.displayName)} · <span class="msi">person</span>${w.occupants} · <span class="msi">star</span>${w.favorites}</div>
                        ${tagsHtml}
                    </div>
                </div>
            </div>`;
        });
        worldsGridHtml += `</div>`;
    } else {
        worldsGridHtml = `<div class="empty-msg">${t('profiles.content.no_public_worlds', 'No public worlds found.')}</div>`;
    }

    const userId = d.id || '';
    const contentHtml = `
        <div class="fd-content-pills">
            <button class="fd-tab fd-content-pill active" id="fdWorldsPill" onclick="switchFdContentPill('worlds',this)">${tf('profiles.content.worlds_pill', { count: allUserWorlds.length }, 'Worlds ({count})')}</button>
            <button class="fd-tab fd-content-pill" id="fdAvatarsPill" onclick="switchFdContentPill('avatars',this)">${tf('profiles.content.avatars_pill', { count: 0 }, 'Avatars (0)')}</button>
        </div>
        <div id="fdContentWorlds">${worldsGridHtml}</div>
        <div id="fdContentAvatars" style="display:none;" data-user-id="${esc(userId)}">
            <div class="empty-msg">${t('profiles.content.loading_avatars', 'Loading avatars...')}</div>
        </div>`;


    c.innerHTML = `${bannerHtml}<div class="fd-content${bannerSrc ? ' fd-has-banner' : ''}"><div class="fd-header">${imgTag}<div><div class="fd-name">${esc(d.displayName)}</div>${pronounsHtml}<div class="fd-status" id="fd-live-status"><span class="${fdDotClass} ${fdStatusDotCls}" style="width:8px;height:8px;"></span>${fdIsOffline ? t('status.offline', 'Offline') : statusLabel(d.status)}${(!fdIsOffline && fdIsWeb) ? ' ' + t('profiles.friends.web_suffix', '(Web)') : ''}${(!fdIsOffline && d.statusDescription) ? ' - ' + esc(d.statusDescription) : ''}</div></div></div>${badgesHtml}${actionsHtml}${tabsHtml}<div id="fdTabInfo">${infoContent}</div><div id="fdTabGroups" style="display:none;">${groupsContent}</div><div id="fdTabMutuals" style="display:none;">${mutualsContent}</div><div id="fdTabContent" style="display:none;">${contentHtml}</div><div id="fdTabFavs" style="display:none;" data-user-id="${esc(userId)}"></div><div style="margin-top:10px;text-align:right;"><button class="vrcn-button-round" onclick="closeFriendDetail()">${t('common.close', 'Close')}</button></div></div>`;

    // Auto-lookup avatar ID from avtrdb if we have a file_ ID (chip-only, no modal open)
    if (avatarFileId) sendToCS({ action: 'vrcLookupAvatarByFileId', fileId: avatarFileId, openModal: false });
    else if (avatarId && avatarId.startsWith('avtr_')) sendToCS({ action: 'vrcGetAvatarInfo', avatarId });

    requestAnimationFrame(() => {
        const bio = c.querySelector('.fd-bio');
        const btn = c.querySelector('.fd-bio-expand');
        if (bio && btn && bio.scrollHeight > bio.clientHeight + 2) btn.style.display = '';
    });

    c.querySelectorAll('.fd-group-card-meta').forEach(el => {
        let text = (el.textContent || '').replace(/\s*(?:\u00C2\u00B7|\u00B7)\s*/g, ' \u00B7 ').trim();
        text = text.replace(/(\d+)\s+members/gi, (_, count) => tf('worlds.groups.members', { count }, '{count} members'));
        text = text.replace(/\bGroup\b/g, t('groups.common.group', 'Group'));
        el.textContent = text;
    });
    c.querySelectorAll('.s-card-sub').forEach(el => {
        el.innerHTML = el.innerHTML.replace(/\u00C2\u00B7/g, '&middot;').replace(/\u00B7/g, '&middot;');
    });

    // Pre-fetch avatars for Content tab count
    if (userId) sendToCS({ action: 'vrcGetUserAvatars', userId: userId });

    // Request mini-timeline events for this user
    if (userId) { _fdTimelineEvents = []; sendToCS({ action: 'getTimelineForUser', userId }); }

    // Live ticker - only when friend is confirmed in same instance (never for own profile)
    if (_fdLiveTimer) { clearInterval(_fdLiveTimer); _fdLiveTimer = null; }
    if (d.inSameInstance && !(currentVrcUser && d.id === currentVrcUser.id)) {
        let liveSecs = d.totalTimeSeconds;
        _fdLiveTimer = setInterval(() => {
            liveSecs++;
            const el = document.getElementById('fdTimeTogether');
            if (el) el.textContent = formatDuration(liveSecs);
            else { clearInterval(_fdLiveTimer); _fdLiveTimer = null; }
        }, 1000);
    }
}

function renderFdTimeline(userId, events) {
    if (!currentFriendDetail || currentFriendDetail.id !== userId) return;
    const el = document.getElementById('fdMiniTl');
    if (!el) return;

    _fdTimelineEvents = events || [];

    if (!_fdTimelineEvents.length) {
        el.innerHTML = `<div style="padding:4px 0;font-size:12px;color:var(--tx3);">${t('timeline.empty.initial', 'No events yet')}</div>`;
        return;
    }

    el.innerHTML = _fdTimelineEvents.map(ev => {
        const meta  = typeof tlTypeMeta === 'function' ? tlTypeMeta(ev.type) : { icon: 'event', label: ev.type };
        const color = { instance_join:'var(--accent)', photo:'var(--ok)', first_meet:'var(--cyan)', meet_again:'#AB47BC', notification:'var(--warn)', avatar_switch:'#FF7043', video_url:'#29B6F6' }[ev.type] || 'var(--tx3)';
        const d     = new Date(ev.timestamp);
        const dt    = `${typeof tlFormatShortDate === 'function' ? tlFormatShortDate(d) : d.toLocaleDateString()} | ${typeof tlFormatTime === 'function' ? tlFormatTime(d) : d.toLocaleTimeString()}`;
        const ei    = ev.id.replace(/'/g, "\\'");
        return `<div style="display:flex;align-items:center;gap:8px;padding:5px 2px;border-bottom:1px solid var(--brd);cursor:pointer;" onclick="openTlDetail('${ei}')">
            <span style="font-size:11px;color:var(--tx3);white-space:nowrap;">${esc(dt)}</span>
            <span class="msi" style="font-size:14px;color:${color};flex-shrink:0;">${meta.icon}</span>
            <span style="font-size:12px;">${esc(meta.label)}</span>
        </div>`;
    }).join('');
}

function friendAction(action, location, userId) {
    const btnContainer = document.querySelector('.fd-actions');
    if (btnContainer) btnContainer.querySelectorAll('button').forEach(b => b.disabled = true);
    if (action === 'join') sendToCS({ action: 'vrcJoinFriend', location: location });
    else if (action === 'invite') sendToCS({ action: 'vrcInviteFriend', userId: userId });
    else if (action === 'requestInvite') sendToCS({ action: 'vrcRequestInvite', userId: userId });
}

function confirmUnfriend(userId, displayName) {
    const btn = document.getElementById('fdUnfriend');
    if (!btn) return;
    if (btn.dataset.confirm) {
        btn.disabled = true;
        btn.innerHTML = '<span class="msi" style="font-size:14px;">hourglass_empty</span>';
        sendToCS({ action: 'vrcUnfriend', userId: userId });
    } else {
        btn.dataset.confirm = '1';
        btn.innerHTML = `<span style="font-size:11px;font-weight:600;">${t('profiles.actions.confirm', 'Confirm?')}</span>`;
        setTimeout(() => {
            if (btn && !btn.disabled) {
                delete btn.dataset.confirm;
                btn.innerHTML = '<span class="msi" style="font-size:16px;">person_remove</span>';
            }
        }, 4000);
    }
}


// Favorite Friends

function toggleFavFriend(userId, fvrtId, btn) {
    const isFav = btn.classList.contains('active');
    btn.disabled = true;
    if (isFav) {
        sendToCS({ action: 'vrcRemoveFavoriteFriend', userId, fvrtId });
    } else {
        sendToCS({ action: 'vrcAddFavoriteFriend', userId });
    }
}

function handleFavFriendToggled(payload) {
    const { userId, fvrtId, isFavorited } = payload;
    // Update in-memory list
    favFriendsData = favFriendsData.filter(f => f.favoriteId !== userId);
    if (isFavorited) favFriendsData.push({ fvrtId, favoriteId: userId });
    // Update button if profile is open
    const btn = document.getElementById('fdFavBtn');
    if (btn) {
        btn.disabled = false;
        btn.classList.toggle('active', isFavorited);
        btn.title = isFavorited ? t('profiles.actions.unfavorite', 'Unfavorite') : t('profiles.actions.favorite', 'Favorite');
        btn.innerHTML = `<span class="msi" style="font-size:16px;">${isFavorited ? 'star' : 'star_outline'}</span>`;
    }
    // Refresh favorites grid if visible
    filterFavFriends();
    // Refresh sidebar so FAVORITES section updates immediately
    renderVrcFriends(vrcFriendsData);
}

// People Tab: Favorites / Search / Blocked / Muted

function setPeopleFilter(filter) {
    peopleFilter = filter;
    document.getElementById('peopleFilterFav').classList.toggle('active', filter === 'favorites');
    document.getElementById('peopleFilterAll').classList.toggle('active', filter === 'all');
    document.getElementById('peopleFilterSearch').classList.toggle('active', filter === 'search');
    document.getElementById('peopleFilterBlocked').classList.toggle('active', filter === 'blocked');
    document.getElementById('peopleFilterMuted').classList.toggle('active', filter === 'muted');
    document.getElementById('peopleFavArea').style.display     = filter === 'favorites' ? '' : 'none';
    document.getElementById('peopleAllArea').style.display     = filter === 'all'       ? '' : 'none';
    document.getElementById('peopleSearchArea').style.display  = filter === 'search'    ? '' : 'none';
    document.getElementById('peopleBlockedArea').style.display = filter === 'blocked'   ? '' : 'none';
    document.getElementById('peopleMutedArea').style.display   = filter === 'muted'     ? '' : 'none';
    refreshPeopleTab();
}

function refreshPeopleTab() {
    if (peopleFilter === 'favorites') sendToCS({ action: 'vrcGetFavoriteFriends' });
    if (peopleFilter === 'all')       filterAllFriends();
    if (peopleFilter === 'blocked')   sendToCS({ action: 'vrcGetBlocked' });
    if (peopleFilter === 'muted')     sendToCS({ action: 'vrcGetMuted' });
}

function filterAllFriends() {
    const el = document.getElementById('allFriendsGrid');
    if (!el) return;
    const q = (document.getElementById('allFriendSearchInput')?.value || '').toLowerCase();
    let friends = q
        ? vrcFriendsData.filter(f => (f.displayName || '').toLowerCase().includes(q))
        : [...vrcFriendsData];
    friends.sort((a, b) => (a.displayName || '').localeCompare(b.displayName || ''));
    if (!friends.length) {
        el.innerHTML = `<div class="empty-msg">${q ? t('profiles.people.no_results', 'No results') : t('profiles.people.no_friends', 'No friends yet')}</div>`;
        return;
    }
    el.innerHTML = friends.map(f => {
        const img = f.image ? `<div class="fav-friend-av" style="background-image:url('${cssUrl(f.image)}')"></div>`
                            : `<div class="fav-friend-av fav-friend-av-letter">${esc((f.displayName || '?')[0].toUpperCase())}</div>`;
        const uid = jsq(f.id);
        return `<div class="fav-friend-card" onclick="openFriendDetail('${uid}')">
            ${img}
            <div class="fav-friend-info">
                <div class="fav-friend-name">${esc(f.displayName)}</div>
                <div class="fav-friend-status">${getFriendStatusLine(f)}</div>
            </div>
        </div>`;
    }).join('');
}

function filterModList(type) {
    const isBlock = type === 'block';
    renderModList(isBlock ? 'blockedList' : 'mutedList', isBlock ? (blockedData || []) : (mutedData || []), type);
}

function renderModList(containerId, list, actionType) {
    const el = document.getElementById(containerId);
    if (!el) return;
    const searchId = actionType === 'block' ? 'blockedSearch' : 'mutedSearch';
    const query = (document.getElementById(searchId)?.value || '').toLowerCase().trim();
    const filtered = query ? (list || []).filter(e => (e.targetDisplayName || e.targetUserId || '').toLowerCase().includes(query)) : (list || []);
    if (!filtered.length) {
        el.innerHTML = `<div class="empty-msg">${query ? t('profiles.people.no_results', 'No results') : (actionType === 'block' ? t('profiles.people.no_blocked', 'No blocked users') : t('profiles.people.no_muted', 'No muted users'))}</div>`;
        return;
    }
    list = filtered;
    const btnLabel = actionType === 'block' ? t('profiles.people.unblock', 'Unblock') : t('profiles.people.unmute', 'Unmute');
    const btnClass = 'vrcn-button-round vrcn-btn-danger';
    el.innerHTML = list.map(entry => {
        const uid = jsq(entry.targetUserId || '');
        const displayName = entry.targetDisplayName || entry.targetUserId || '?';
        // Use enriched image from API; fall back to friends cache, then letter
        const friend = vrcFriendsData.find(f => f.id === entry.targetUserId);
        const imageUrl = entry.image || (friend && friend.image) || '';
        const img = imageUrl
            ? `<div class="fav-friend-av" style="background-image:url('${cssUrl(imageUrl)}')"></div>`
            : `<div class="fav-friend-av fav-friend-av-letter">${esc(displayName[0].toUpperCase())}</div>`;
        const statusLine = getFriendStatusLine(friend);
        return `<div class="fav-friend-card" onclick="openFriendDetail('${uid}')">
            ${img}
            <div class="fav-friend-info">
                <div class="fav-friend-name">${esc(displayName)}</div>
                <div class="fav-friend-status">${statusLine}</div>
            </div>
            <button class="${btnClass}" style="margin-left:auto;flex-shrink:0;" onclick="event.stopPropagation();doUnmod('${uid}','${actionType}')">${btnLabel}</button>
        </div>`;
    }).join('');

    el.querySelectorAll('.fav-friend-card').forEach((card, index) => {
        const entry = list[index];
        const friend = vrcFriendsData.find(f => f.id === entry?.targetUserId);
        const statusEl = card.querySelector('.fav-friend-status');
        if (statusEl) statusEl.innerHTML = getFriendStatusLine(friend);
    });
}

function doUnmod(userId, type) {
    sendToCS({ action: type === 'block' ? 'vrcUnblock' : 'vrcUnmute', userId });
}

function toggleMod(userId, type, btn) {
    const isActive = btn.classList.contains('active');
    sendToCS({ action: isActive
        ? (type === 'block' ? 'vrcUnblock' : 'vrcUnmute')
        : (type === 'block' ? 'vrcBlock'   : 'vrcMute'),
        userId });
}

function renderFavFriends(list) {
    favFriendsData = Array.isArray(list) ? list : [];
    filterFavFriends();
}

function filterFavFriends() {
    const el = document.getElementById('favFriendsGrid');
    if (!el) return;
    const q = (document.getElementById('favFriendSearchInput')?.value || '').toLowerCase();
    const favIds = new Set(favFriendsData.map(f => f.favoriteId));
    let friends = vrcFriendsData.filter(f => favIds.has(f.id));
    if (q) friends = friends.filter(f => (f.displayName || '').toLowerCase().includes(q));
    if (!friends.length) {
        el.innerHTML = `<div class="empty-msg">${q ? t('profiles.people.no_favorites_match', 'No favorites match your search') : t('profiles.people.no_favorites', 'No favorite friends yet')}</div>`;
        return;
    }
    el.innerHTML = friends.map(f => {
        const img = f.image ? `<div class="fav-friend-av" style="background-image:url('${cssUrl(f.image)}')"></div>`
                            : `<div class="fav-friend-av fav-friend-av-letter">${esc((f.displayName || '?')[0].toUpperCase())}</div>`;
        const uid = jsq(f.id);
        const statusLine = getFriendStatusLine(f);
        return `<div class="fav-friend-card" onclick="openFriendDetail('${uid}')">
            ${img}
            <div class="fav-friend-info">
                <div class="fav-friend-name">${esc(f.displayName)}</div>
                <div class="fav-friend-status">${statusLine}</div>
            </div>
        </div>`;
    }).join('');

    el.querySelectorAll('.fav-friend-card').forEach((card, index) => {
        const statusEl = card.querySelector('.fav-friend-status');
        if (statusEl) statusEl.innerHTML = getFriendStatusLine(friends[index]);
    });
}

// Global badge tooltip (position: fixed, escapes modal overflow)
(function () {
    let tip = null;

    function getTip() {
        if (!tip) {
            tip = document.createElement('div');
            tip.className = 'fd-vrc-badge-tooltip-global';
            document.body.appendChild(tip);
        }
        return tip;
    }

    document.addEventListener('mouseover', function (e) {
        const wrap = e.target.closest('.fd-vrc-badge-wrap');
        if (!wrap) return;
        const t = getTip();
        const img  = wrap.dataset.badgeImg  || '';
        const name = decodeURIComponent(wrap.dataset.badgeName || '');
        const desc = decodeURIComponent(wrap.dataset.badgeDesc || '');
        t.innerHTML =
            `<img class="fd-vrc-badge-tip-img" src="${esc(img)}" alt="">` +
            `<div class="fd-vrc-badge-tip-text">` +
                `<div class="fd-vrc-badge-tip-name">${esc(name)}</div>` +
                (desc ? `<div class="fd-vrc-badge-tip-desc">${esc(desc)}</div>` : '') +
            `</div>`;

        // Measure while invisible to get real dimensions
        t.style.opacity = '0';
        t.style.display = 'flex';
        const tw = t.offsetWidth;
        const th = t.offsetHeight;

        const rect = wrap.getBoundingClientRect();
        let x = rect.left + rect.width / 2 - tw / 2;
        let y = rect.top - th - 8;

        // Clamp to viewport
        x = Math.max(8, Math.min(window.innerWidth - tw - 8, x));
        if (y < 8) y = rect.bottom + 8;

        t.style.left = x + 'px';
        t.style.top  = y + 'px';
        t.style.opacity = '1';
    });

    document.addEventListener('mouseout', function (e) {
        const wrap = e.target.closest('.fd-vrc-badge-wrap');
        if (!wrap) return;
        if (wrap.contains(e.relatedTarget)) return;
        if (tip) tip.style.opacity = '0';
    });
}());

/* === Invite Modal === */
let _invModalUserId      = null;
let _invModalApiMsgs     = []; // { slot, message, canBeUpdated, remainingCooldownMinutes }
let _invModalSelected    = -1;
let _invModalTab         = 'direct'; // 'direct' | 'message' | 'photo'
let _invModalPhotoFileId = null;
let _invModalPhotoUrl    = null;  // CDN url from library selection
let _invModalDisplayName = '';

function openFriendInviteModal(userId, displayName, initialTab) {
    closeFriendInviteModal();
    _invModalUserId      = userId;
    _invModalApiMsgs     = [];
    _invModalSelected    = -1;
    _invModalTab         = 'direct';
    _invModalPhotoFileId = null;
    _invModalPhotoUrl    = null;
    _invModalDisplayName = displayName || '';
    const _invInitialTab = initialTab || 'direct';

    const thumb      = currentInstanceData?.worldThumb || '';
    const worldName  = currentInstanceData?.worldName || t('profiles.invite.your_instance', 'your instance');
    const hasVrcPlus = Array.isArray(currentVrcUser?.tags) && currentVrcUser.tags.includes('system_supporter');
    const inviteTitle = tf('profiles.invite.to', { name: esc(displayName) }, 'Invite {name} to');

    const el = document.createElement('div');
    el.className = 'modal-overlay';
    el.style.zIndex = '10003';
    el.innerHTML = `
        <div class="modal-box inv-single-modal">
            <div class="inv-world-banner" style="${thumb ? `background-image:url('${cssUrl(thumb)}')` : ''}">
                <div class="inv-world-fade"></div>
                <div class="inv-world-info">
                    <div style="font-size:11px;color:rgba(255,255,255,.65);margin-bottom:2px;">${inviteTitle}</div>
                    <div class="inv-world-name">${esc(worldName)}</div>
                </div>
                <button class="inv-close-btn" onclick="closeFriendInviteModal()"><span class="msi" style="font-size:18px;">close</span></button>
            </div>
            <div class="fd-tabs" style="margin:14px 16px 0;flex-shrink:0;">
                <button class="fd-tab active" id="invTab_direct"  onclick="_invModalSetTab('direct')">${t('profiles.invite.tab.direct', 'Directly')}</button>
                <button class="fd-tab"         id="invTab_message" onclick="_invModalSetTab('message')">${t('profiles.invite.tab.message', 'With Message')}</button>
                ${hasVrcPlus ? `<button class="fd-tab" id="invTab_photo" onclick="_invModalSetTab('photo')">${t('profiles.invite.tab.photo', 'With Image')}</button>` : ''}
            </div>
            <div class="inv-single-body">
                <div id="invContent_direct" style="padding:4px 0 6px;font-size:12px;color:var(--tx3);">${t('profiles.invite.direct_description', 'Send a direct invite with no message.')}</div>
                <div id="invMsgSection" style="display:none;">
                    <div id="invMsgOptLabel" style="display:none;font-size:11px;color:var(--tx3);margin-bottom:4px;">${t('profiles.invite.optional_message', 'Optional message')}</div>
                    <div id="invMsgList"></div>
                </div>
                <div id="invPhotoSection" style="display:none;">
                    <label class="gp-label">${t('profiles.invite.image_label', 'Image')} <span style="color:var(--tx3);font-weight:400;">(${t('common.required', 'required')})</span></label>
                    <div id="invLibraryGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(72px,1fr));gap:6px;max-height:180px;overflow-y:auto;padding:4px 0;"></div>
                </div>
                <button id="invSendBtn" class="vrcn-button vrcn-btn-primary inv-action-full" onclick="_invModalSend()">${t('profiles.invite.send', 'Send Invite')}</button>
            </div>
        </div>`;
    el.addEventListener('click', e => { if (e.target === el) closeFriendInviteModal(); });
    document.body.appendChild(el);
    window._inviteModalEl = el;
    if (_invInitialTab !== 'direct') _invModalSetTab(_invInitialTab);
}

function refreshFriendInviteModalTranslations() {
    const el = window._inviteModalEl;
    if (!el) return;
    const titleEl = el.querySelector('.inv-world-info > div:first-child');
    if (titleEl) titleEl.textContent = tf('profiles.invite.to', { name: _invModalDisplayName }, 'Invite {name} to');
    const directTab = document.getElementById('invTab_direct');
    if (directTab) directTab.textContent = t('profiles.invite.tab.direct', 'Directly');
    const messageTab = document.getElementById('invTab_message');
    if (messageTab) messageTab.textContent = t('profiles.invite.tab.message', 'With Message');
    const photoTab = document.getElementById('invTab_photo');
    if (photoTab) photoTab.textContent = t('profiles.invite.tab.photo', 'With Image');
    const directContent = document.getElementById('invContent_direct');
    if (directContent) directContent.textContent = t('profiles.invite.direct_description', 'Send a direct invite with no message.');
    const optionalLabel = document.getElementById('invMsgOptLabel');
    if (optionalLabel) optionalLabel.textContent = t('profiles.invite.optional_message', 'Optional message');
    const photoLabel = document.querySelector('#invPhotoSection .gp-label');
    if (photoLabel) photoLabel.innerHTML = `${t('profiles.invite.image_label', 'Image')} <span style="color:var(--tx3);font-weight:400;">(${t('common.required', 'required')})</span>`;
    const sendBtn = document.getElementById('invSendBtn');
    if (sendBtn) sendBtn.textContent = t('profiles.invite.send', 'Send Invite');
    _invModalRenderMsgs();
    if (_invModalTab === 'photo') {
        const cached = (typeof invFilesCache !== 'undefined') ? invFilesCache['gallery'] : null;
        if (cached) _invModalRenderLibrary(cached);
    }
}

function _invModalSetTab(tab) {
    _invModalTab = tab;
    ['direct', 'message', 'photo'].forEach(t => {
        const btn = document.getElementById(`invTab_${t}`);
        if (btn) btn.classList.toggle('active', t === tab);
    });
    const directEl = document.getElementById('invContent_direct');
    const msgSect  = document.getElementById('invMsgSection');
    const optLabel = document.getElementById('invMsgOptLabel');
    const photoSect = document.getElementById('invPhotoSection');
    if (directEl)  directEl.style.display  = tab === 'direct'  ? '' : 'none';
    if (msgSect)   msgSect.style.display   = (tab === 'message' || tab === 'photo') ? '' : 'none';
    if (optLabel)  optLabel.style.display  = tab === 'photo'   ? '' : 'none';
    if (photoSect) photoSect.style.display = tab === 'photo'   ? '' : 'none';
    // Load messages on first switch to a tab that needs them
    if ((tab === 'message' || tab === 'photo') && !_invModalApiMsgs.length) {
        sendToCS({ action: 'vrcGetInviteMessages' });
    } else if (tab === 'message' || tab === 'photo') {
        _invModalRenderMsgs();
    }
    // On first switch to photo tab: load library from cache or request
    if (tab === 'photo') {
        const cached = (typeof invFilesCache !== 'undefined') && invFilesCache['gallery'];
        if (cached && cached.length > 0) _invModalRenderLibrary(cached);
        else sendToCS({ action: 'invGetFiles', tag: 'gallery' });
    }
    _invModalUpdateSendBtn();
}

function _invModalRenderMsgs() {
    const list = document.getElementById('invMsgList');
    if (!list) return;
    if (!_invModalApiMsgs.length) {
        list.innerHTML = `<div class="inv-msg-loading"><span class="msi" style="font-size:16px;animation:spin 1s linear infinite;">progress_activity</span> ${t('profiles.invite.loading_messages', 'Loading messages...')}</div>`;
        return;
    }
    list.innerHTML = _invModalApiMsgs.map(m => {
        const i        = m.slot;
        const canEdit  = m.canBeUpdated;
        const cooldown = m.remainingCooldownMinutes || 0;
        const isSelected = _invModalSelected === i;
        return `
        <div class="inv-msg-item${isSelected ? ' selected' : ''}" id="invMsg_${i}" onclick="_invModalSelectMsg(${i})">
            <span class="inv-msg-text" id="invMsgText_${i}">${esc(m.message)}</span>
            ${canEdit
                ? `<button class="inv-msg-edit" onclick="event.stopPropagation();_invModalEditMsg(${i})" title="${esc(t('common.edit', 'Edit'))}"><span class="msi" style="font-size:14px;">edit</span></button>`
                : `<span class="inv-msg-cooldown" title="${esc(tf('profiles.invite.cooldown_title', { count: cooldown }, '{count} min cooldown'))}"><span class="msi" style="font-size:13px;">schedule</span></span>`}
        </div>`;
    }).join('');
}

function handleVrcInviteMessages(msgs) {
    _invModalApiMsgs = (msgs || []).slice().sort((a, b) => a.slot - b.slot);
    _invModalRenderMsgs();
}

function handleVrcInviteMessageUpdateFailed(payload) {
    const itemEl = document.getElementById(`invMsg_${payload.slot}`);
    if (itemEl) { delete itemEl.dataset.editing; _invModalRenderMsgs(); }
    showToast(false, tf('profiles.invite.cooldown_toast', { count: payload.cooldown || 60 }, 'Cooldown: {count} min remaining'));
}

function _invModalSelectMsg(idx) {
    _invModalSelected = _invModalSelected === idx ? -1 : idx;
    document.querySelectorAll('#invMsgList .inv-msg-item').forEach(el => {
        el.classList.toggle('selected', parseInt(el.id.replace('invMsg_', '')) === _invModalSelected);
    });
    _invModalUpdateSendBtn();
}

function _invModalEditMsg(idx) {
    const itemEl = document.getElementById(`invMsg_${idx}`);
    const textEl = document.getElementById(`invMsgText_${idx}`);
    if (!itemEl || !textEl || itemEl.dataset.editing) return;
    itemEl.dataset.editing = '1';
    const cur = (_invModalApiMsgs.find(m => m.slot === idx) || {}).message || '';
    textEl.outerHTML = `<input class="inv-msg-input" id="invMsgText_${idx}" value="${cur.replace(/"/g, '&quot;')}"
        onblur="_invModalSaveMsg(${idx},this)"
        onkeydown="if(event.key==='Enter')this.blur();if(event.key==='Escape'){this.dataset.cancel='1';this.blur();}">`;
    const inp = document.getElementById(`invMsgText_${idx}`);
    if (inp) { inp.focus(); inp.select(); }
    const icon = itemEl.querySelector('.inv-msg-edit .msi');
    if (icon) icon.textContent = 'check';
}

function _invModalSaveMsg(idx, input) {
    const itemEl = document.getElementById(`invMsg_${idx}`);
    if (!itemEl) return;
    delete itemEl.dataset.editing;
    const m = _invModalApiMsgs.find(x => x.slot === idx);
    const newText = input.value.trim();
    if (!input.dataset.cancel && newText && m && newText !== m.message) {
        sendToCS({ action: 'vrcUpdateInviteMessage', slot: idx, message: newText });
        m.message = newText;
        m.canBeUpdated = false;
        m.remainingCooldownMinutes = 60;
    }
    _invModalRenderMsgs();
}

function getInviteUploadTileHtml() {
    return `<div style="width:100%;aspect-ratio:1;border-radius:6px;cursor:pointer;background:var(--bg-input);border:1.5px dashed var(--border);display:flex;align-items:center;justify-content:center;flex-shrink:0;" onmouseover="this.style.borderColor='var(--accent)'" onmouseout="this.style.borderColor='var(--border)'" onclick="_invModalOpenUpload()" title="${esc(t('profiles.invite.upload_new_photo', 'Upload new photo'))}"><span class="msi" style="font-size:22px;color:var(--tx3);pointer-events:none;">add_photo_alternate</span></div>`;
}

function _invModalOpenUpload() {
    openInvUploadModal('photos', file => {
        _invModalRenderLibrary(invFilesCache['gallery'] || []);
        const firstImg = document.querySelector('#invLibraryGrid img');
        if (firstImg) _invModalSelectLibraryPhoto(firstImg, file.fileUrl, file.id);
    });
}

function _invModalOnGalleryLoaded(files) {
    if (!document.getElementById('invLibraryGrid')) return;
    _invModalRenderLibrary(files);
}

function _invModalRenderLibrary(files) {
    const grid = document.getElementById('invLibraryGrid');
    if (!grid) return;
    if (!files || !files.length) { grid.innerHTML = getInviteUploadTileHtml(); return; }
    grid.innerHTML = getInviteUploadTileHtml() + files.map(f => {
        const url = f.fileUrl || '';
        const fid = jsq(f.id || '');
        if (!url) return '';
        return `<img src="${esc(url)}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:0.85;transition:opacity .15s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=0.85" onclick="_invModalSelectLibraryPhoto(this,'${jsq(url)}','${fid}')" onerror="this.style.display='none'">`;
    }).join('');
}

function _invModalSelectLibraryPhoto(el, url, fileId) {
    document.querySelectorAll('#invLibraryGrid img').forEach(i => i.style.outline = 'none');
    el.style.outline = '2px solid var(--accent)';
    _invModalPhotoFileId = fileId;
    _invModalPhotoUrl    = url;
    _invModalUpdateSendBtn();
}

function _invModalUpdateSendBtn() {
    const btn = document.getElementById('invSendBtn');
    if (!btn) return;
    btn.disabled = (_invModalTab === 'message' && _invModalSelected < 0)
                || (_invModalTab === 'photo'   && !_invModalPhotoUrl);
}

function _invModalSend() {
    if (!_invModalUserId) return;
    if (_invModalTab === 'direct') {
        sendToCS({ action: 'vrcInviteFriend', userId: _invModalUserId });
    } else if (_invModalTab === 'message') {
        if (_invModalSelected < 0) return;
        sendToCS({ action: 'vrcInviteFriend', userId: _invModalUserId, messageSlot: _invModalSelected });
    } else if (_invModalTab === 'photo') {
        if (!_invModalPhotoUrl) return;
        const p = { action: 'vrcInviteFriendWithPhoto', userId: _invModalUserId, fileUrl: _invModalPhotoUrl };
        if (_invModalSelected >= 0) p.messageSlot = _invModalSelected;
        sendToCS(p);
    }
    closeFriendInviteModal();
}

function closeFriendInviteModal() {
    const el = window._inviteModalEl;
    if (!el) return;
    el.style.opacity = '0';
    el.style.transition = 'opacity .15s';
    setTimeout(() => el.remove(), 150);
    window._inviteModalEl = null;
}

// ================================================================
// Image Picker - shared modal for profile/group icon and banner
// ================================================================

let _pickerContext = null; // { type: 'profile-icon'|'profile-banner'|'group-icon'|'group-banner', targetId }

function openImagePicker(type, targetId) {
    _pickerContext = { type, targetId: targetId || null };
    window._pickerSelectedUrl = null;

    const isIcon = type.endsWith('-icon');
    const tag    = isIcon ? 'icon' : 'gallery';
    const title  = isIcon
        ? t('profiles.picker.select_icon', 'Select Icon')
        : t('profiles.picker.select_banner', 'Select Banner Photo');

    // Build overlay fresh each time so it's always above current modal stack
    let overlay = document.getElementById('imagePickerOverlay');
    if (overlay) overlay.remove();

    overlay = document.createElement('div');
    overlay.id = 'imagePickerOverlay';
    overlay.style.cssText = 'position:fixed;inset:0;z-index:10003;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;animation:fadeIn .12s ease;backdrop-filter:blur(4px);';
    overlay.innerHTML = `
        <div class="gp-modal" style="width:460px;max-height:80vh;display:flex;flex-direction:column;">
            <div class="gp-modal-header">
                <span class="msi" style="font-size:20px;color:var(--accent);">edit</span>
                <span id="imagePickerTitle">${esc(title)}</span>
                <button class="vrcn-button-round" onclick="closeImagePicker()" title="${esc(t('common.close', 'Close'))}"><span class="msi" style="font-size:18px;">close</span></button>
            </div>
            <div class="gp-modal-body" style="flex:1;overflow-y:auto;">
                <div id="imagePickerGrid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(80px,1fr));gap:6px;padding:4px 0;">
                    <div style="grid-column:1/-1;text-align:center;padding:20px;font-size:11px;color:var(--tx3);">${t('common.loading', 'Loading...')}</div>
                </div>
            </div>
            <div class="gp-modal-footer">
                <button class="vrcn-button-round" onclick="closeImagePicker()">${t('common.cancel', 'Cancel')}</button>
                <button class="vrcn-button-round vrcn-btn-join" id="imagePickerApply" disabled onclick="applyImagePicker()" style="opacity:.45;">
                    <span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">check</span>${t('common.apply', 'Apply')}
                </button>
            </div>
        </div>`;
    document.body.appendChild(overlay);
    overlay.addEventListener('click', e => { if (e.target === overlay) closeImagePicker(); });

    // Always fetch fresh to avoid stale or mismatched inventory cache data.
    const pickerGrid = document.getElementById('imagePickerGrid');
    if (pickerGrid) pickerGrid.dataset.state = 'loading';
    sendToCS({ action: 'invGetFiles', tag });
}

function _renderImagePickerGrid(files) {
    const grid = document.getElementById('imagePickerGrid');
    if (!grid) return;
    if (!files || !files.length) {
        grid.dataset.state = 'empty';
        grid.innerHTML = `<div style="grid-column:1/-1;text-align:center;padding:20px;font-size:11px;color:var(--tx3);">${t('profiles.picker.no_items', 'No items found.')}<br>${t('profiles.picker.upload_via_inventory', 'Upload images via the Inventory tab.')}</div>`;
        return;
    }
    grid.dataset.state = 'loaded';
    grid.innerHTML = files.map(f => {
        const url = f.fileUrl || '';
        const fid = f.id || '';
        if (!url) return '';
        return `<img src="${esc(url)}" data-file-id="${esc(fid)}" style="width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;cursor:pointer;opacity:.85;transition:opacity .15s,outline .12s;" onmouseover="this.style.opacity=1" onmouseout="this.style.opacity=.85" onclick="selectPickerImage(this,'${jsq(url)}','${jsq(fid)}')" onerror="this.parentElement?.remove()">`;
    }).join('');
}

function selectPickerImage(el, url, fileId) {
    document.querySelectorAll('#imagePickerGrid img').forEach(i => { i.style.opacity = '.85'; i.style.outline = 'none'; });
    el.style.opacity = '1';
    el.style.outline = '2px solid var(--accent)';
    window._pickerSelectedUrl = url;
    window._pickerSelectedFileId = fileId || '';
    const btn = document.getElementById('imagePickerApply');
    if (btn) { btn.disabled = false; btn.style.opacity = '1'; }
}

function closeImagePicker() {
    const overlay = document.getElementById('imagePickerOverlay');
    if (overlay) overlay.remove();
    _pickerContext = null;
    window._pickerSelectedUrl = null;
    window._pickerSelectedFileId = null;
}

function refreshImagePickerTranslations() {
    const overlay = document.getElementById('imagePickerOverlay');
    if (!overlay || !_pickerContext) return;
    const isIcon = _pickerContext.type.endsWith('-icon');
    const titleEl = document.getElementById('imagePickerTitle');
    if (titleEl) titleEl.textContent = isIcon
        ? t('profiles.picker.select_icon', 'Select Icon')
        : t('profiles.picker.select_banner', 'Select Banner Photo');
    const closeBtn = overlay.querySelector('.gp-modal-header .vrcn-button-round');
    if (closeBtn) closeBtn.title = t('common.close', 'Close');
    const cancelBtn = overlay.querySelector('.gp-modal-footer .vrcn-button-round');
    if (cancelBtn) cancelBtn.textContent = t('common.cancel', 'Cancel');
    const applyBtn = document.getElementById('imagePickerApply');
    if (applyBtn) applyBtn.innerHTML = `<span class="msi" style="font-size:16px;vertical-align:middle;margin-right:4px;">check</span>${t('common.apply', 'Apply')}`;
    const grid = document.getElementById('imagePickerGrid');
    if (!grid || grid.querySelector('img')) return;
    if (grid.dataset.state === 'empty') {
        grid.innerHTML = `<div style="grid-column:1/-1;text-align:center;padding:20px;font-size:11px;color:var(--tx3);">${t('profiles.picker.no_items', 'No items found.')}<br>${t('profiles.picker.upload_via_inventory', 'Upload images via the Inventory tab.')}</div>`;
    } else {
        grid.innerHTML = `<div style="grid-column:1/-1;text-align:center;padding:20px;font-size:11px;color:var(--tx3);">${t('common.loading', 'Loading...')}</div>`;
    }
}

function applyImagePicker() {
    const url    = window._pickerSelectedUrl;
    const fileId = window._pickerSelectedFileId;
    if (!url || !_pickerContext) return;
    const { type, targetId } = _pickerContext;

    // Profile: pass URL (VRChat user API accepts CDN URLs directly)
    if (type === 'profile-icon')        sendToCS({ action: 'vrcUpdateProfile', userIcon: url });
    else if (type === 'profile-banner') sendToCS({ action: 'vrcUpdateProfile', profilePicOverride: url });
    // Groups: VRChat group API requires file IDs (iconId/bannerId), not URLs
    else if (type === 'group-icon')     sendToCS({ action: 'vrcUpdateGroup', groupId: targetId, iconId:   fileId });
    else if (type === 'group-banner')   sendToCS({ action: 'vrcUpdateGroup', groupId: targetId, bannerId: fileId });

    closeImagePicker();
}

// Called from messages.js when invFiles arrives while picker is open and waiting
function onImagePickerFilesLoaded(files, tag) {
    const overlay = document.getElementById('imagePickerOverlay');
    if (!overlay || !_pickerContext) return;
    const expectedTag = _pickerContext.type.endsWith('-icon') ? 'icon' : 'gallery';
    if (tag !== expectedTag) return;
    _renderImagePickerGrid(files);
}
