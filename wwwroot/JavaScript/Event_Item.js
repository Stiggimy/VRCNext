/* === Calendar Event Detail Modal === */

function openEventDetail(groupId, calendarId) {
    if (!groupId || !calendarId) return;
    const el = document.getElementById('detailModalContent');
    el.innerHTML = sk('detail');
    document.getElementById('modalDetail').style.display = 'flex';
    sendToCS({ action: 'vrcGetCalendarEvent', groupId, calendarId });
}

function renderEventDetail(ev) {
    const el = document.getElementById('detailModalContent');
    if (!ev || !ev.id) {
        el.innerHTML = `<div style="padding:40px;text-align:center;color:var(--tx3);">${t('calendar.detail.not_found', 'Event not found')}</div>`;
        return;
    }

    const bannerSrc = ev.imageUrl || 'fallback_cover.png';
    const bannerHtml = `<div class="fd-banner"><img src="${bannerSrc}" onerror="this.src='fallback_cover.png'"><div class="fd-banner-fade"></div></div>`;

    const start = ev.startsAt ? new Date(ev.startsAt) : null;
    const end = ev.endsAt ? new Date(ev.endsAt) : null;
    const dateLine = start && !isNaN(start) ? fmtLongDate(start) : '';
    const endDiffDay = end && !isNaN(end) && start &&
        (end.getFullYear() !== start.getFullYear() || end.getMonth() !== start.getMonth() || end.getDate() !== start.getDate());
    const timeLine = start && !isNaN(start)
        ? fmtTime(start) + (end && !isNaN(end) ? ' – ' + (endDiffDay ? fmtLongDate(end) + ', ' : '') + fmtTime(end) : '')
        : '';

    const tags = Array.isArray(ev.tags) ? ev.tags : [];
    const tagsHtml = tags.map(tag => {
        const isFeatured = /featured/i.test(tag);
        return `<span class="vrcn-badge${isFeatured ? ' warn' : ''}">${esc(tag)}</span>`;
    }).join('');

    const { cls: accessCls } = getInstanceBadge((ev.accessType || '').toLowerCase());
    const accessBadge = ev.accessType
        ? `<span class="vrcn-badge ${accessCls}">${esc(ev.accessType)}</span>`
        : '';

    // Resolve group info: prefer ev.group, fall back to local myGroups cache
    const myGroupsList = (typeof myGroups !== 'undefined') ? myGroups : [];
    const gid = ev.ownerId || ev.groupId || '';
    const groupCache = myGroupsList.find(g => g.id === gid) || {};
    const groupName = ev.group?.name || groupCache.name || '';
    const groupIconUrl = ev.group?.iconUrl || groupCache.iconUrl || '';
    const groupOpenId = jsq(ev.group?.id || gid);

    const groupTopHtml = groupName
        ? `<div style="display:flex;align-items:center;gap:6px;margin-bottom:2px;">
               ${groupIconUrl ? `<img src="${groupIconUrl}" style="width:16px;height:16px;border-radius:3px;object-fit:cover;flex-shrink:0;">` : `<span class="msi" style="font-size:14px;color:var(--tx3);">group</span>`}
               <span style="font-size:11px;color:var(--tx2);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(groupName)}</span>
           </div>`
        : '';

    const groupHtml = groupName
        ? `<div class="fd-section-label" style="margin-top:12px;">${t('calendar.detail.organizer', 'Organizer')}</div>
           <div style="display:flex;align-items:center;gap:8px;cursor:pointer;padding:6px 0;" onclick="openGroupDetail('${groupOpenId}')">
               ${groupIconUrl ? `<img src="${groupIconUrl}" style="width:28px;height:28px;border-radius:6px;object-fit:cover;">` : ''}
               <span style="font-size:13px;color:var(--tx1);">${esc(groupName)}</span>
               <span class="msi" style="font-size:14px;color:var(--tx3);">chevron_right</span>
           </div>`
        : '';

    const isFollowing = ev.userInterest?.isFollowing === true;
    const groupId = esc(gid);
    const calendarId = esc(ev.id || '');
    const followBtnId = `evFollowBtn_${ev.id}`;
    const followLabel = isFollowing
        ? t('calendar.detail.unfollow', 'Unfollow')
        : t('calendar.detail.follow', 'Follow');

    el.innerHTML = `
        ${bannerHtml}
        <div class="fd-content${bannerHtml ? ' fd-has-banner' : ''}">
            <div class="fd-header" style="flex-direction:column;align-items:flex-start;gap:6px;">
                ${groupTopHtml}
                <div class="fd-name" style="font-size:18px;">${esc(ev.title || t('calendar.untitled_event', 'Untitled Event'))}</div>
                ${dateLine ? `<div style="font-size:12px;color:var(--tx2);display:flex;align-items:center;gap:4px;"><span class="msi" style="font-size:14px;">calendar_today</span>${esc(dateLine)}</div>` : ''}
                ${timeLine ? `<div style="font-size:12px;color:var(--tx2);display:flex;align-items:center;gap:4px;"><span class="msi" style="font-size:14px;">schedule</span>${esc(timeLine)}</div>` : ''}
                <div style="display:flex;flex-wrap:wrap;gap:4px;margin-top:2px;">${accessBadge}${tagsHtml}</div>
            </div>
            ${groupHtml}
            ${ev.description ? `<div class="fd-section-label" style="margin-top:12px;">${t('calendar.detail.about', 'About')}</div><div class="fd-bio">${esc(ev.description)}</div>` : ''}
            <div style="display:flex;justify-content:flex-start;align-items:center;gap:8px;margin-top:16px;">
                <button class="vrcn-button-round vrcn-btn-join" id="${followBtnId}" onclick="toggleFollowEvent('${groupId}','${calendarId}',${isFollowing},this)"><span class="msi">${isFollowing ? 'notifications_off' : 'notifications_active'}</span><span class="ev-follow-lbl">${followLabel}</span></button>
                ${gid ? `<button class="vrcn-button-round" onclick="openGroupDetail('${groupOpenId}')"><span class="msi">group</span><span>${t('calendar.detail.open_group', 'Open Group')}</span></button>` : ''}
                <button class="vrcn-button-round" onclick="document.getElementById('modalDetail').style.display='none'" style="margin-left:auto;">${t('common.close', 'Close')}</button>
            </div>
        </div>`;
}

function toggleFollowEvent(groupId, calendarId, isCurrentlyFollowing, btn) {
    const follow = !isCurrentlyFollowing;
    sendToCS({ action: 'vrcFollowEvent', groupId, calendarId, follow });
    if (btn) {
        btn.querySelector('.msi').textContent = follow ? 'notifications_off' : 'notifications_active';
        btn.querySelector('.ev-follow-lbl').textContent = follow
            ? t('calendar.detail.unfollow', 'Unfollow')
            : t('calendar.detail.follow', 'Follow');
        btn.onclick = () => toggleFollowEvent(groupId, calendarId, follow, btn);
    }
}
